using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace SoundConvertClient.Views;

public partial class FirmwareFlashWindow : Window
{
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(2) };
    public FirmwareFlashViewModel ViewModel { get; }
    public FirmwareFlashWindow() : this(new FirmwareFlashViewModel()) { }
    public FirmwareFlashWindow(FirmwareFlashViewModel viewModel)
    {
        InitializeComponent(); ViewModel = viewModel; DataContext = viewModel;
        viewModel.RequestFile = async () =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new()
            {
                Title = "选择 RP2350 UF2 固件", AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("UF2 固件") { Patterns = ["*.uf2"] }]
            });
            return files.Count == 0 ? null : files[0].TryGetLocalPath() ?? throw new IOException("请选择本电脑上的 UF2 文件。");
        };
        // Match the main window's isolated preview convention.
        if (Environment.GetEnvironmentVariable("UAB_UI_PREVIEW") == "1")
        {
            ShowActivated = false; ShowInTaskbar = false;
            Opened += (_, _) => Position = new Avalonia.PixelPoint(-10000, -10000);
        }
        timer.Tick += async (_, _) => await viewModel.RefreshAsync();
        Opened += async (_, _) => { timer.Start(); await viewModel.RefreshAsync(); };
        Closing += (_, e) => { if (viewModel.IsWriting) e.Cancel = true; };
        Closed += (_, _) => { timer.Stop(); viewModel.Dispose(); };
    }
    void CloseClicked(object? sender, RoutedEventArgs e) => Close();
}
