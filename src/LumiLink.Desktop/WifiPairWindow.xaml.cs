using System.Windows;

namespace LumiLink.Desktop;

public partial class WifiPairWindow : Window
{
    private readonly Func<string, string, string, Task> _go;
    private bool _busy;

    public WifiPairWindow(string ipHint, Func<string, string, string, Task> go)
    {
        InitializeComponent();
        _go = go;
        IpBox.Text = ipHint;
    }

    private async void BtnGo_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        ErrLabel.Text = "";
        if (!int.TryParse(PortBox.Text.Trim(), out var port) || port is <= 0 or > 65535)
        { ErrLabel.Text = "Porta inválida."; return; }
        var code = CodeBox.Text.Trim();
        if (code.Length != 6 || !code.All(char.IsDigit)) { ErrLabel.Text = "Código: 6 dígitos."; return; }
        _busy = true;
        try
        {
            await _go(IpBox.Text.Trim(), port.ToString(), code);
            Close();
        }
        catch (Exception ex) { ErrLabel.Text = $"Falhou: {ex.Message}"; }
        finally { _busy = false; }
    }
}
