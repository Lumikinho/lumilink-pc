using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using LumiLink.Desktop.Clipboard;
using LumiLink.Desktop.Profile;
using LumiLink.Desktop.Storage;

namespace LumiLink.Desktop;

public sealed class ClipRow
{
    public long Id { get; }
    public string Kind { get; }
    public string Title { get; }
    public string Sub { get; }
    public BitmapImage? Thumb { get; }
    public ClipRow(long id, string kind, string title, string sub, BitmapImage? thumb)
    { Id = id; Kind = kind; Title = title; Sub = sub; Thumb = thumb; }
}

public partial class ClipHistoryWindow : Window
{
    private readonly ClipStore _store;
    private readonly ClipMonitor _monitor;

    public ClipHistoryWindow(ClipStore store, ClipMonitor monitor)
    {
        InitializeComponent();
        _store = store; _monitor = monitor;
        ChkSync.IsChecked = App.Settings.ClipSync;
        Refresh();
    }

    private void Refresh()
    {
        ClipList.ItemsSource = _store.List(100).Select(i =>
        {
            var when = DateTimeOffset.FromUnixTimeMilliseconds(i.Ts).ToLocalTime().ToString("g");
            return i.Kind switch
            {
                "text" => new ClipRow(i.Id, i.Kind,
                    i.Text.Length > 60 ? i.Text[..60] + "…" : i.Text, $"{when} · de {i.Source}", null),
                "image" => new ClipRow(i.Id, i.Kind, $"Imagem ({i.Size / 1024} KB)",
                    $"{when} · de {i.Source}", ImageUtils.LoadPhoto(i.Path)),
                _ => new ClipRow(i.Id, i.Kind, i.Name, $"{i.Size / 1024} KB · {when} · de {i.Source}", null),
            };
        }).ToList();
    }

    private ClipRow? Selected => ClipList.SelectedItem as ClipRow;

    private void BtnCopy_Click(object sender, RoutedEventArgs e) => CopySelected();
    private void ClipList_DoubleClick(object sender, RoutedEventArgs e) => CopySelected();

    private void CopySelected()
    {
        var row = Selected;
        if (row is null) return;
        var item = _store.Get(row.Id);
        if (item is null) return;
        if (item.Kind == "text") _monitor.SetText(item.Text);
        else if (item.Kind == "image" && File.Exists(item.Path))
            _monitor.SetImage(File.ReadAllBytes(item.Path));
        else if (item.Kind == "file" && File.Exists(item.Path))
            _monitor.SetFiles(new[] { item.Path });
    }

    private void BtnDelete_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not null) { _store.Delete(Selected.Id); Refresh(); }
    }

    private void BtnClear_Click(object sender, RoutedEventArgs e)
    {
        _store.Clear();
        Refresh();
    }

    private void BtnRefresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private void ChkSync_Changed(object sender, RoutedEventArgs e)
    {
        App.Settings.ClipSync = ChkSync.IsChecked == true;
        try
        {
            File.WriteAllText(Path.Combine(App.AppDir, "settings.json"),
                System.Text.Json.JsonSerializer.Serialize(App.Settings,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
