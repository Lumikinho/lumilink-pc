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
}
