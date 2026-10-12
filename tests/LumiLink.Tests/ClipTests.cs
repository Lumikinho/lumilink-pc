using LumiLink.Desktop.Storage;
using Xunit;

namespace LumiLink.Tests;

// Clipboard: histórico SQLite.
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
    public void FileAssemblerOrderAndVerify()
    {
        var dir = Path.Combine(_dir, "parts");
        var data = new byte[] { 1, 2, 3, 4, 5 };
        using var s = new LumiLink.Desktop.Transfer.FileAssembler(
            dir, "f1", "a.bin", 5, LumiLink.Desktop.Transfer.FileAssembler.ShaHex(data));
        Assert.True(s.Feed(1, new byte[] { 4, 5 })); // futuro: bufferiza
        Assert.Equal(0, s.ExpectedSeq);
        Assert.True(s.Feed(0, new byte[] { 1, 2, 3 })); // drena 0 e 1
        Assert.Equal(2, s.ExpectedSeq);
        Assert.False(s.Feed(0, new byte[] { 9 })); // duplicado/atrasado
        Assert.True(s.VerifyAndClose(out var hex));
        Assert.Equal(LumiLink.Desktop.Transfer.FileAssembler.ShaHex(data), hex);
    }

    [Fact]
    public void FileAssemblerBadHash()
    {
        var dir = Path.Combine(_dir, "parts2");
        using var s = new LumiLink.Desktop.Transfer.FileAssembler(dir, "f2", "b.bin", 2, "deadbeef");
        s.Feed(0, new byte[] { 1, 2 });
        Assert.False(s.VerifyAndClose(out _));
    }
}
