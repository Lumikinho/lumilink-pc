using System.IO;
using System.Security.Cryptography;

namespace LumiLink.Desktop.Transfer;

// Montagem de arquivo recebido por chunks (ordem estrita). Testável.
public sealed class FileAssembler : IDisposable
{
    public string FileId { get; }
    public string Name { get; }
    public long Size { get; }
    public string Sha256 { get; }
    public int ExpectedSeq { get; private set; }
    public DateTime LastActivity { get; private set; } = DateTime.UtcNow;

    private readonly FileStream _out;
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

    public string PartPath => _out.Name;

    public bool Feed(int seq, byte[] data)
    {
        if (seq != ExpectedSeq) return false;
        _out.Write(data, 0, data.Length);
        ExpectedSeq++;
        LastActivity = DateTime.UtcNow;
        return true;
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
