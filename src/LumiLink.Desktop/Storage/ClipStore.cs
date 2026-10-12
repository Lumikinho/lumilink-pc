using Microsoft.Data.Sqlite;
using System.IO;

namespace LumiLink.Desktop.Storage;

// Histórico da área de transferência em SQLite. Blobs (imagem/arquivo) em clips/.
public sealed record ClipItem(long Id, long Ts, string Kind, string Text, string Mime,
    string Path, string Name, long Size, string Sha, string Source, string Peer);

public sealed class ClipStore : IDisposable
{
    private readonly SqliteConnection _db;
    private readonly string _clipDir;
    private readonly object _lock = new();

    public ClipStore(string appDir)
    {
        _clipDir = Path.Combine(appDir, "clips");
        Directory.CreateDirectory(_clipDir);
        _db = new SqliteConnection($"Data Source={Path.Combine(appDir, "clips.db")}");
        _db.Open();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS clips (
              id INTEGER PRIMARY KEY AUTOINCREMENT, ts INTEGER NOT NULL,
              kind TEXT NOT NULL, text TEXT DEFAULT '', mime TEXT DEFAULT '',
              path TEXT DEFAULT '', name TEXT DEFAULT '', size INTEGER DEFAULT 0,
              sha TEXT DEFAULT '', source TEXT DEFAULT '', peer TEXT DEFAULT '');
            CREATE INDEX IF NOT EXISTS idx_clips_ts ON clips(ts DESC);
            """;
        cmd.ExecuteNonQuery();
    }

    public string ClipDir => _clipDir;

    public ClipItem AddText(string text, string source, string peer)
        => Insert("text", text, "", "", "", text.Length, "", source, peer);

    public ClipItem AddImage(string mime, string path, long size, string sha, string source, string peer)
        => Insert("image", "", mime, path, Path.GetFileName(path), size, sha, source, peer);

    public ClipItem AddFile(string name, string path, long size, string sha, string source, string peer)
        => Insert("file", "", "", path, name, size, sha, source, peer);

    private ClipItem Insert(string kind, string text, string mime, string path, string name,
        long size, string sha, string source, string peer)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT INTO clips (ts,kind,text,mime,path,name,size,sha,source,peer) " +
                "VALUES ($ts,$k,$t,$m,$p,$n,$s,$h,$src,$peer); SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            cmd.Parameters.AddWithValue("$k", kind);
            cmd.Parameters.AddWithValue("$t", text);
            cmd.Parameters.AddWithValue("$m", mime);
            cmd.Parameters.AddWithValue("$p", path);
            cmd.Parameters.AddWithValue("$n", name);
            cmd.Parameters.AddWithValue("$s", size);
            cmd.Parameters.AddWithValue("$h", sha);
            cmd.Parameters.AddWithValue("$src", source);
            cmd.Parameters.AddWithValue("$peer", peer);
            var id = (long)cmd.ExecuteScalar()!;
            return Get(id)!;
        }
    }

    public ClipItem? Get(long id)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT id,ts,kind,text,mime,path,name,size,sha,source,peer FROM clips WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", id);
            using var r = cmd.ExecuteReader();
            return r.Read() ? Read(r) : null;
        }
    }

    public List<ClipItem> List(int limit = 100)
    {
        lock (_lock)
        {
            var out_ = new List<ClipItem>();
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT id,ts,kind,text,mime,path,name,size,sha,source,peer FROM clips ORDER BY id DESC LIMIT $n";
            cmd.Parameters.AddWithValue("$n", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read()) out_.Add(Read(r));
            return out_;
        }
    }

    public void Delete(long id)
    {
        lock (_lock)
        {
            var item = Get(id);
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "DELETE FROM clips WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
            try
            {
                if (item is not null && item.Path.Length > 0 && File.Exists(item.Path)
                    && Path.GetDirectoryName(item.Path) == _clipDir) File.Delete(item.Path);
            }
            catch { }
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "DELETE FROM clips";
            cmd.ExecuteNonQuery();
            try
            {
                foreach (var f in Directory.GetFiles(_clipDir)) File.Delete(f);
            }
            catch { }
        }
    }

    private static ClipItem Read(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetString(4),
        r.GetString(5), r.GetString(6), r.GetInt64(7), r.GetString(8), r.GetString(9), r.GetString(10));

    public void Dispose() => _db.Dispose();
}
