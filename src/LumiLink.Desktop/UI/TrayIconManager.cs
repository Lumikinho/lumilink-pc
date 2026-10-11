using System.Drawing;
using System.Windows.Forms;

namespace LumiLink.Desktop.UI;

// Bandeja: tooltip mostra "Conectado a <nome>", duplo clique restaura.
public sealed class TrayIconManager : IDisposable
{
    private readonly NotifyIcon _icon;
    public TrayIconManager(string name, Action show, Action exit)
    {
        _icon = new NotifyIcon
        {
            Text = "LumiLink — Desconectado",
            Icon = SystemIcons.Application,
            Visible = true,
            ContextMenuStrip = new ContextMenuStrip()
        };
        _icon.ContextMenuStrip.Items.Add("Abrir", null, (_, _) => show());
        _icon.ContextMenuStrip.Items.Add("Sair", null, (_, _) => { _icon.Visible = false; Environment.Exit(0); });
        _icon.DoubleClick += (_, _) => show();
    }
    public void SetTooltip(string state)
    {
        var t = $"LumiLink — {state}";
        _icon.Text = t.Length > 63 ? t[..63] : t;
    }
    public void Dispose() { _icon.Visible = false; _icon.Dispose(); }
}
