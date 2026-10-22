using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using UsbAudioBridge;

namespace SoundConvertClient;

/// <summary>Local USB utility. Does not connect to the Engine or send board commands.</summary>
public sealed class FirmwareFlashViewModel : INotifyPropertyChanged, IDisposable
{
    readonly IBootselDriveAccess access;
    readonly CancellationTokenSource lifetime = new();
    FirmwareUf2? image;
    BootselDrive? selectedDrive;
    bool loading, refreshing, writing, disposed;
    string status = "请选择 UF2 固件，并按说明将板卡接入 BOOTSEL 模式。", error = "", filePath = "", log = "";
    int progress, transferGeneration;
    public event PropertyChangedEventHandler? PropertyChanged;
    public Func<Task<string?>>? RequestFile { get; set; }
    public ObservableCollection<BootselDrive> Drives { get; } = [];
    public UiCommand ChooseFileCommand { get; }
    public UiCommand RefreshCommand { get; }
    public UiCommand FlashCommand { get; }
    public BootselDrive? SelectedDrive { get => selectedDrive; set { if (selectedDrive == value) return; selectedDrive = value; Changed(); RefreshState(); } }
    public bool IsWriting => writing;
    public bool IsLoading => loading;
    public bool IsRefreshing => refreshing;
    public bool CanClose => !writing;
    public bool CanChoose => !writing && !loading && !disposed;
    public bool CanRefresh => !writing && !refreshing && !disposed;
    public bool CanSelectDrive => !writing && !disposed;
    public bool CanFlash => !writing && !loading && !disposed && image is not null && SelectedDrive is not null;
    public bool HasFile => image is not null;
    public bool HasError => error.Length != 0;
    public string Error => error;
    public string Status => status;
    public string FilePath => filePath;
    public string FileSummary => image is null ? "尚未选择有效固件" : $"{image.FileName} · {image.FileBytes:N0} 字节 · RP2350 Arm-S";
    public string FileDetails => image is null ? "" : $"Flash：0x{FirmwareUf2.FlashStart:X8}–0x{image.AddressEnd - 1:X8}\n有效固件：{image.PayloadBytes:N0} 字节；末尾 8 KiB 配置区不在写入范围内。";
    public string Sha256 => image?.Sha256 ?? "";
    public string DriveSummary => Drives.Count == 0 ? "未发现 RP2350 启动盘。请使用 USB 数据线，按住 BOOTSEL 接入本电脑后刷新。" : $"发现 {Drives.Count} 个 RP2350 启动盘，请选择要烧写的板卡。";
    public int Progress => progress;
    public string Log => log;

    public FirmwareFlashViewModel(IBootselDriveAccess? access = null)
    {
        this.access = access ?? new WindowsBootselDrives();
        ChooseFileCommand = new(async _ => { if (RequestFile is not null) { try { if (await RequestFile() is { } path) await LoadFileAsync(path); } catch (Exception e) { Fail("选择文件失败", e); } } }, () => CanChoose);
        RefreshCommand = new(async _ => await RefreshAsync(), () => CanRefresh);
        FlashCommand = new(async _ => await FlashAsync(), () => CanFlash);
    }

    public async Task LoadFileAsync(string path)
    {
        if (!CanChoose) return;
        loading = true; image = null; filePath = path; error = ""; progress = 0;
        status = "正在校验 UF2 文件…"; RefreshState(); FileChanged();
        try
        {
            var loaded = await FirmwareUf2.LoadAsync(path, lifetime.Token);
            if (disposed) return;
            image = loaded; status = "UF2 结构与写入范围校验通过，请选择目标启动盘并开始烧写。";
            AddLog($"已校验 {loaded.FileName}，SHA256 {loaded.Sha256}");
        }
        catch (OperationCanceledException) when (disposed) { }
        catch (Exception e) { if (!disposed) Fail("固件校验失败", e); }
        finally { loading = false; FileChanged(); RefreshState(); }
    }

    public async Task RefreshAsync()
    {
        if (!CanRefresh) return;
        refreshing = true; RefreshState();
        try
        {
            var found = await access.ScanAsync(lifetime.Token);
            if (disposed) return;
            if (!Drives.SequenceEqual(found))
            {
                var previous = SelectedDrive;
                Drives.Clear(); foreach (var drive in found) Drives.Add(drive);
                SelectedDrive = found.FirstOrDefault(d => d == previous);
                Changed(nameof(DriveSummary));
            }
        }
        catch (OperationCanceledException) when (disposed) { }
        catch (Exception e) { if (!disposed) Fail("启动盘扫描失败", e); }
        finally { refreshing = false; RefreshState(); }
    }

    public async Task FlashAsync()
    {
        if (!CanFlash || image is not { } firmware || SelectedDrive is not { } target) return;
        writing = true; error = ""; progress = 0; status = "正在核对目标启动盘并传输固件，请保持 USB 连接…";
        int generation = ++transferGeneration;
        AddLog($"开始传输 {firmware.FileName} → {target.DisplayName}"); RefreshState();
        try
        {
            var reporter = new Progress<int>(value => { if (writing && transferGeneration == generation) { progress = Math.Max(progress, value); Changed(nameof(Progress)); } });
            await new BootselFirmwareWriter(access).TransferAsync(firmware, target, reporter);
            progress = 100;
            status = "UF2 文件传输完成。板卡通常会自动重启，请重新连接设备确认固件版本和运行状态。";
            AddLog("文件写入、刷新与关闭已完成；此结果不包含 Flash 读回校验或板卡启动验证。");
            SelectedDrive = null;
        }
        catch (Exception e)
        {
            Fail("传输未完成或结果无法确认", e);
            AddLog("请按住 BOOTSEL 重新接入、刷新并选择板卡后重试。");
            SelectedDrive = null;
        }
        finally { writing = false; RefreshState(); }
        await RefreshAsync();
    }

    void FileChanged() { foreach (string name in new[] { nameof(FilePath), nameof(FileSummary), nameof(FileDetails), nameof(Sha256), nameof(HasFile) }) Changed(name); }
    void Fail(string action, Exception e) { error = $"{action}：{e.Message}"; status = action; AddLog(error); RefreshState(); }
    void AddLog(string text) { log += $"[{DateTime.Now:HH:mm:ss}] {text}\n"; if (log.Length > 16000) log = log[^12000..]; Changed(nameof(Log)); }
    void RefreshState()
    {
        foreach (string name in new[] { nameof(IsWriting), nameof(IsLoading), nameof(IsRefreshing), nameof(CanClose), nameof(CanChoose), nameof(CanRefresh), nameof(CanSelectDrive), nameof(CanFlash), nameof(Status), nameof(Error), nameof(HasError), nameof(Progress) }) Changed(name);
        ChooseFileCommand.Refresh(); RefreshCommand.Refresh(); FlashCommand.Refresh();
    }
    void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    public void Dispose() { if (disposed) return; if (writing) throw new InvalidOperationException("固件传输期间不能关闭窗口。"); disposed = true; lifetime.Cancel(); lifetime.Dispose(); }
}
