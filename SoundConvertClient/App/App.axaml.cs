using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
namespace SoundConvertClient;

public partial class App:Application
{
    public static event Action<MainWindow>? WindowCreated;
    public override void Initialize()=>AvaloniaXamlLoader.Load(this);
    public override void OnFrameworkInitializationCompleted()
    {
        if(ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window=new MainWindow();desktop.MainWindow=window;WindowCreated?.Invoke(window);
        }
        base.OnFrameworkInitializationCompleted();
    }
}

