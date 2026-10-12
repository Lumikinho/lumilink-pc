using LumiLink.Desktop.Clipboard;
using LumiLink.Desktop.Storage;
using Xunit;

namespace LumiLink.Tests;

// Clipboard: histórico SQLite + montagem de arquivo por chunks.
public sealed class ClipTests : IDisposable
{
    private readonly string _dir;

    public ClipTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "llclip-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void HistoryAddListDeleteClear()
    {
        using var store = new ClipStore(_dir);
        var t = store.AddText("olá", "a", "b");
        Assert.True(t.Id > 0);
        Assert.Equal("olá", store.Get(t.Id)!.Text);
        store.AddImage("image/png", "/tmp/x.png", 10, "h", "a", "b");
        Assert.Equal(2, store.List(10).Count);
        Assert.Equal("image", store.List(10)[0].Kind); // mais recente primeiro
        store.Delete(t.Id);
        Assert.Null(store.Get(t.Id));
        store.Clear();
        Assert.Empty(store.List(10));
    }

    [Fact]
    public void FileSessionOrderVerify()
    {
        var dir = Path.Combine(_dir, "parts");
        using var s = new ClipFileSession(dir, "f1", "a.bin", 5, ClipFileSession.ShaHex(new byte[] { 1, 2, 3, 4, 5 }));
        Assert.False(s.Feed(1, new byte[] { 9 })); // fora de ordem
        Assert.True(s.Feed(0, new byte[] { 1, 2, 3 }));
        Assert.True(s.Feed(1, new byte[] { 4, 5 }));
        Assert.True(s.VerifyAndClose(out var hex));
        Assert.Equal(ClipFileSession.ShaHex(new byte[] { 1, 2, 3, 4, 5 }), hex);
    }

    [Fact]
    public void FileSessionBadHash()
    {
        var dir = Path.Combine(Path.GetTempPath(), "llclip2-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            using var s = new ClipFileSession(dir, "f2", "b.bin", 2, "deadbeef");
            s.Feed(0, new byte[] { 1, 2 });
            Assert.False(s.VerifyAndClose(out _));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
