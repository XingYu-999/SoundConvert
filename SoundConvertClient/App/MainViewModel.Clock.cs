namespace SoundConvertClient;

public sealed partial class MainViewModel
{
    public int ClockModeIndex{get=>Get<int>(nameof(ClockModeIndex));set=>SetRemote(nameof(ClockModeIndex),value);}
    public System.Collections.Generic.IReadOnlyList<string> ClockModes=>Get<string[]>(nameof(ClockModes))??[];
    public string ClockStatus=>Get<string>(nameof(ClockStatus));
}
