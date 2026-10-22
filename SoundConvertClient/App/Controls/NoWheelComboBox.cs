using Avalonia.Controls;
using Avalonia.Input;

namespace SoundConvertClient.Controls;

public sealed class NoWheelComboBox : ComboBox
{
    protected override Type StyleKeyOverride => typeof(ComboBox);

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        // Leave closed selectors unchanged and let the surrounding page scroll.
        // The open popup keeps its normal list scrolling behavior.
        if (IsDropDownOpen)
            base.OnPointerWheelChanged(e);
    }
}
