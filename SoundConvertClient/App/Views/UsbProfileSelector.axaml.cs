using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;

namespace SoundConvertClient.Views;
public partial class UsbProfileSelector : UserControl
{
    INotifyPropertyChanged? observed;
    bool updating;
    public UsbProfileSelector()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (observed is not null) observed.PropertyChanged -= StateChanged;
            observed = DataContext as INotifyPropertyChanged;
            if (observed is not null) observed.PropertyChanged += StateChanged;
            UpdateCards();
        };
    }
    void StateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IBridgeView.UsbDeviceProfile) or nameof(IBridgeView.UsbDeviceProfiles) or nameof(IBridgeView.CanEditUsbProfile)) UpdateCards();
    }
    void UpdateCards()
    {
        if (DataContext is not IBridgeView view) return;
        updating = true;
        try
        {
            foreach (var card in this.GetLogicalDescendants().OfType<RadioButton>())
            {
                string value = card.Tag?.ToString() ?? "";
                card.IsChecked = view.UsbDeviceProfile == value;
                card.IsEnabled = view.CanEditUsbProfile && view.UsbDeviceProfiles.Contains(value);
                ToolTip.SetTip(card, view.UsbDeviceProfiles.Contains(value) ? "选择仅修改草稿；保存后重新枚举 USB。" : "当前固件未提供此模式。");
                ToolTip.SetShowOnDisabled(card, true);
            }
        }
        finally { updating = false; }
    }
    void ProfileChanged(object? sender, RoutedEventArgs e)
    {
        if (updating || sender is not RadioButton { IsChecked: true, Tag: string value } card || DataContext is not IBridgeView view) return;
        if (card.IsEnabled && view.UsbDeviceProfiles.Contains(value)) view.UsbDeviceProfile = value;
    }
}
