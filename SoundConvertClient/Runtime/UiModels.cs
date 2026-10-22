using System.Windows.Input;
using UsbAudioBridge;
namespace SoundConvertClient;

public sealed class UiCommand(Action<object?> action,Func<bool>? canExecute=null) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => canExecute?.Invoke()??true;
    public void Execute(object? parameter) { if(CanExecute(parameter))action(parameter); }
    public void Refresh()=>CanExecuteChanged?.Invoke(this,EventArgs.Empty);
}

public sealed record RememberedBoard(string DeviceId, string Name, string Address, string LocalAddress,
    int ControlPort = 49161, string? UsbSerial = null, DateTimeOffset? LastConnected = null, AudioFormat? LastHostFormat = null, string? AdapterId = null);

public sealed class RememberedBoardRow(RememberedBoard board, ICommand connect, ICommand forget)
{
    public RememberedBoard Board { get; } = board;
    public string Name => Board.Name;
    public string Address => Board.Address;
    public string Detail => $"{Board.Address}:{Board.ControlPort} · {Board.DeviceId[..Math.Min(12, Board.DeviceId.Length)]}";
    public string LastConnected => Board.LastConnected is { } time ? $"上次连接 {time.LocalDateTime:MM-dd HH:mm}" : "已记住虚拟声卡";
    public ICommand ConnectCommand { get; } = connect;
    public ICommand ForgetCommand { get; } = forget;
}
