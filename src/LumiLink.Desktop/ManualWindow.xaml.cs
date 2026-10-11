using System.Windows;

namespace LumiLink.Desktop;

public partial class ManualWindow : Window
{
    private readonly Func<string, int, string, Task> _pair;

    public ManualWindow(Func<string, int, string, Task> pair)
    {
        InitializeComponent();
        _pair = pair;
    }

    private async void BtnGo_Click(object sender, RoutedEventArgs e)
    {
        ErrLabel.Text = "";
        if (!int.TryParse(PortBox.Text.Trim(), out var port) || port is <= 0 or > 65535)
        { ErrLabel.Text = "Porta inválida."; return; }
        var code = CodeBox.Text.Trim();
        if (code.Length != 6 || !code.All(char.IsDigit)) { ErrLabel.Text = "Código: 6 dígitos."; return; }
        try
        {
            await _pair(IpBox.Text.Trim(), port, code);
            Close();
        }
        catch (Exception ex) { ErrLabel.Text = $"Falhou: {ex.Message}"; }
    }
}
