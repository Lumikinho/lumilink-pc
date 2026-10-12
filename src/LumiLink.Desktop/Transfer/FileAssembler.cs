using System.IO;
using System.Security.Cryptography;

namespace LumiLink.Desktop.Transfer;

// Montagem tolerante a reordenação: guarda futuros até 256 e drena em ordem.
public sealed class FileAssembler : IDisposable
{
    private const int MaxBuffered = 256;

    public string FileId { get; }
    public string Name { get; }
    public long Size { get; }
    public string Sha256 { get; }
    public int ExpectedSeq { get; private set; }
    public DateTime LastActivity { get; private set; } = DateTime.UtcNow;

    private readonly FileStream _out;
    private readonly SortedDictionary<int, byte[]> _pending = new();
    private bool _disposed;

    public FileAssembler(string dir, string fileId, string name, long size, string sha256)
    {
        FileId = fileId;
        Name = string.IsNullOrWhiteSpace(name) ? fileId : Path.GetFileName(name);
        Size = size;
        Sha256 = sha256;
        Directory.CreateDirectory(dir);
        _out = File.Create(Path.Combine(dir, fileId + ".part"));
    }

    public string PartPath => ((FileStream)_out).Name;
    public int Buffered => _pending.Count;

    // false = duplicado/atrasado demais (descarta) ou buffer cheio.
    public bool Feed(int seq, byte[] data)
    {
        if (seq < ExpectedSeq) return false;
        if (seq == ExpectedSeq)
        {
            Write(data);
            while (_pending.TryGetValue(ExpectedSeq, out var next))
            {
                _pending.Remove(ExpectedSeq);
                Write(next);
            }
            return true;
        }
        if (_pending.Count >= MaxBuffered || _pending.ContainsKey(seq)) return false;
        _pending[seq] = data;
        LastActivity = DateTime.UtcNow;
        return true;
    }

    private void Write(byte[] data)
    {
        _out.Write(data, 0, data.Length);
        ExpectedSeq++;
        LastActivity = DateTime.UtcNow;
    }

    public bool IdleFor(TimeSpan t) => DateTime.UtcNow - LastActivity > t;

    public bool VerifyAndClose(out string actualHex)
    {
        _out.Flush();
        _out.Close();
        using var fs = File.OpenRead(PartPath);
        using var sha = SHA256.Create();
        actualHex = Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
        return string.Equals(actualHex, Sha256, StringComparison.OrdinalIgnoreCase)
            && new FileInfo(PartPath).Length == Size;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _out.Close(); } catch { }
        try { if (File.Exists(PartPath)) File.Delete(PartPath); } catch { }
    }

    public static string ShaHex(byte[] data)
        => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
}
