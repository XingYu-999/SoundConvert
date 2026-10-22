using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SoundConvertClient.Views;

public partial class IpAddressDialog : Window
{
    public IpAddressDialog() { InitializeComponent(); Closing += (_, e) => { if (DataContext is IpAddressEditor { Busy: true }) e.Cancel = true; }; }
    public IpAddressDialog(IpAddressEditor editor) : this() { DataContext = editor; }
    void CloseClicked(object? sender, RoutedEventArgs e) => Close();
}
