using Avalonia.Controls;
using Avalonia.Input;

namespace SoundConvertClient.Views;
public partial class ManualConnectDialog : Window
{
    bool completed;
    public ManualConnectDialog() { InitializeComponent(); }
    public ManualConnectDialog(ManualConnectViewModel editor) : this()
    {
        DataContext = editor;
        editor.CloseRequested += () => { completed = true; Close(); };
        Opened += (_, _) => this.FindControl<TextBox>("AddressBox")?.Focus();
        Closing += (_, e) => { if (!completed && editor.Busy) { e.Cancel = true; _ = editor.CancelAsync(); } };
        KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter && editor.CanSubmit) { e.Handled = true; await editor.SubmitAsync(); }
            else if (e.Key == Key.Escape) { e.Handled = true; await editor.CancelAsync(); }
        };
    }
}
