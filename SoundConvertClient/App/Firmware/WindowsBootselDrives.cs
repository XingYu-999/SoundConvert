using System.Runtime.InteropServices;
using System.Text;
using UsbAudioBridge;

namespace SoundConvertClient;

public sealed class WindowsBootselDrives : IBootselDriveAccess
{
    public Task<IReadOnlyList<BootselDrive>> ScanAsync(CancellationToken cancellationToken = default) => Task.Run<IReadOnlyList<BootselDrive>>(() =>
    {
        var found = new List<BootselDrive>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { if (Read(drive.Name) is { } target) found.Add(target); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { /* Detached or inaccessible volumes are not candidates. */ }
        }
        return found.OrderBy(d => d.RootPath, StringComparer.OrdinalIgnoreCase).ToArray();
    }, cancellationToken);

    public Task<Stream> OpenFirmwareAsync(BootselDrive target) => Task.Run<Stream>(() =>
    {
        var current = Read(target.RootPath);
        if (current is null || current != target)
            throw new IOException("所选启动盘已断开或身份发生变化，请刷新并重新选择板卡。");
        // Use the captured volume GUID, not a drive letter that might be reassigned.
        return new FileStream(target.VolumeIdentity + "FIRMWARE.UF2", FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 16384, FileOptions.Asynchronous | FileOptions.WriteThrough);
    });

    static BootselDrive? Read(string root)
    {
        if (root.Length != 3 || !char.IsAsciiLetter(root[0]) || root[1] != ':' || root[2] != '\\') return null;
        var drive = new DriveInfo(root);
        if (drive.DriveType != DriveType.Removable || !drive.IsReady) return null;
        var identity = new StringBuilder(64);
        if (!GetVolumeNameForVolumeMountPoint(root, identity, identity.Capacity))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        string volume = identity.ToString();
        var info = new FileInfo(volume + "INFO_UF2.TXT");
        if (!info.Exists || info.Length is <= 0 or > 8192 || (info.Attributes & FileAttributes.ReparsePoint) != 0) return null;
        string text = File.ReadAllText(info.FullName);
        if (!TryReadBoardId(text, out string boardId)) return null;
        return new(root, volume, drive.VolumeLabel, boardId);
    }

    public static bool TryReadBoardId(string text, out string boardId)
    {
        boardId = "";
        if (!text.Contains("UF2 Bootloader", StringComparison.OrdinalIgnoreCase)) return false;
        foreach (string line in text.Split('\n'))
        {
            if (!line.StartsWith("Board-ID:", StringComparison.OrdinalIgnoreCase)) continue;
            string value = line[9..].Trim();
            if (value.Equals("RP2350", StringComparison.OrdinalIgnoreCase) || value.StartsWith("RP2350-", StringComparison.OrdinalIgnoreCase))
            { boardId = value; return true; }
        }
        return false;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetVolumeNameForVolumeMountPointW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetVolumeNameForVolumeMountPoint(string mountPoint, StringBuilder volumeName, int bufferLength);
}
