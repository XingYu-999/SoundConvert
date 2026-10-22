using Avalonia.Controls;
using Avalonia.Data.Converters;
using System.Globalization;
namespace SoundConvertClient.Views;
public partial class OverviewPage : UserControl { public OverviewPage() { InitializeComponent(); } }

public sealed class UsbDeviceProfileNameConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        "desktop_speaker" => "音箱",
        "speaker" => "通用音箱",
        "headphones" => "耳机",
        "headset" => "耳麦",
        _ => "未知"
    };
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
