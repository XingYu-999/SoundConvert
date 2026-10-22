using System.Windows.Input;
namespace SoundConvertClient;
public sealed partial class MainViewModel
{
    public string[] IndicatorModeOptions => Get<string[]>(nameof(IndicatorModeOptions)) ?? [];
    public int IndicatorModeIndex { get => Get<int>(nameof(IndicatorModeIndex)); set => SetRemote(nameof(IndicatorModeIndex), value); }
    public bool IndicatorConfigurationSupported => Get<bool>(nameof(IndicatorConfigurationSupported));
    public bool CanEditIndicator => Get<bool>(nameof(CanEditIndicator));
    public bool CanIdentifyIndicator => Get<bool>(nameof(CanIdentifyIndicator));
    public string IndicatorConfigurationState => Get<string>(nameof(IndicatorConfigurationState));
    public string IndicatorModeDescription => Get<string>(nameof(IndicatorModeDescription));
    public ICommand IdentifyIndicatorCommand => Command(nameof(IdentifyIndicatorCommand));
}
