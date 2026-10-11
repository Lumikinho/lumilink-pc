using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;

namespace LumiLink.Desktop.UI;

// Ponte com o Compartilhar nativo do Windows (Share Sheet) via
// IDataTransferManagerInterop (WPF, sem empacotamento).
// Escopo desta fase: APENAS exibir o dispositivo conectado (nome, estado, foto).
public static class WindowsShareHelper
{
    [ComImport]
    [Guid("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDataTransferManagerInterop
    {
        IntPtr GetForWindow([In] IntPtr appWindow, [In] ref Guid riid);
        void ShowShareUIForWindow(IntPtr appWindow);
    }

    private static readonly Guid DtmIid =
        new(0xa5caee9b, 0x8708, 0x49d1, 0x8d, 0x36, 0x67, 0xd2, 0x5a, 0x8d, 0xa0, 0x0c);

    private static string _title = "LumiLink";
    private static string _description = "Dispositivo conectado";
    private static string _text = "";
    private static string? _photoPath;
    private static bool _hooked;
    private static readonly object _lock = new();
    private static DataTransferManager? _dtm; // segurar ref: sem isso o GC mata e o DataRequested nunca dispara
    private static IDataTransferManagerInterop? _interop;

    public static void ShareDevice(Window owner, string name, string state, string details, string? photoPath)
    {
        try
        {
            lock (_lock)
            {
                _title = $"LumiLink — {name}";
                _description = $"Dispositivo {state}";
                _text = $"{name} ({state})\n{details}";
                _photoPath = photoPath;
            }

            IntPtr hwnd = new WindowInteropHelper(owner).Handle;
            _interop = DataTransferManager.As<IDataTransferManagerInterop>();
            var iid = DtmIid;
            _dtm = WinRT.MarshalInterface<DataTransferManager>.FromAbi(_interop.GetForWindow(hwnd, ref iid));
            lock (_lock)
            {
                if (!_hooked)
                {
                    _dtm.DataRequested += OnDataRequested;
                    _hooked = true;
                }
            }
            _interop.ShowShareUIForWindow(hwnd);
        }
        catch (Exception ex)
        {
            Desktop.MainWindow.Log($"[share] abrir falhou: {ex.Message}");
            System.Windows.MessageBox.Show($"Compartilhar falhou: {ex.Message}");
        }
    }

    private static async void OnDataRequested(DataTransferManager sender, DataRequestedEventArgs args)
    {
        string title, description, text;
        string? photo;
        lock (_lock) { title = _title; description = _description; text = _text; photo = _photoPath; }
        var deferral = args.Request.GetDeferral();
        try
        {
            var data = args.Request.Data;
            data.Properties.Title = title;
            data.Properties.Description = description;
            data.SetText(text);
            if (!string.IsNullOrEmpty(photo))
            {
                try
                {
                    var file = await StorageFile.GetFileFromPathAsync(photo);
                    data.Properties.Thumbnail = RandomAccessStreamReference.CreateFromFile(file);
                }
                catch { /* sem foto: segue só texto */ }
            }
            Desktop.MainWindow.Log($"[share] dados oferecidos: {title}");
        }
        catch (Exception ex)
        {
            Desktop.MainWindow.Log($"[share] dados falharam: {ex.Message}");
        }
        finally
        {
            deferral.Complete();
        }
    }
}
