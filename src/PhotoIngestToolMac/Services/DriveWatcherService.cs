using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PhotoIngestTool.Services;

/// <summary>
/// EventArgs khi phát hiện thẻ nhớ cắm vào hoặc rút ra.
/// </summary>
public class RemovableDriveEventArgs : EventArgs
{
    public string DriveRoot { get; init; } = string.Empty;
    public string VolumeLabel { get; init; } = string.Empty;
    public long TotalSizeBytes { get; init; }
    public IReadOnlyList<string> MediaFiles { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Module 1 (macOS & Cross-Platform):
/// - Tự động theo dõi thư mục /Volumes trên macOS (hoặc Removable Drive) khi cắm thẻ nhớ SD / CFexpress / XQD.
/// - Lọc file Media hợp lệ và bỏ qua toàn bộ file rác hệ thống của macOS (._*, .DS_Store, .Spotlight-V100, .Trashes...).
/// - Tự động đẩy thẻ nhớ an toàn (Auto-Eject) bằng lệnh chuẩn `diskutil eject` trên macOS.
/// </summary>
public sealed class DriveWatcherService : IDisposable
{
    public static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".CR3", ".CR2", ".ARW", ".NEF", ".RAF", ".DNG",
        ".JPG", ".JPEG", ".MP4", ".MOV"
    };

    public static readonly HashSet<string> RawExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".CR3", ".CR2", ".ARW", ".NEF", ".RAF", ".DNG"
    };

    private static readonly HashSet<string> IgnoredDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "System Volume Information",
        "$RECYCLE.BIN",
        ".Spotlight-V100",
        ".Trashes",
        ".fseventsd",
        ".TemporaryItems",
        ".DocumentRevisions-V100",
        "MISC",
        "THM"
    };

    private static readonly HashSet<string> IgnoredMacVolumes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Macintosh HD",
        "Macintosh HD - Data",
        "Preboot",
        "Recovery",
        "VM",
        "Update",
        "Data",
        "com.apple.TimeMachine.localsnapshots"
    };

    private readonly HashSet<string> _knownMountedCards = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _knownLock = new();
    private CancellationTokenSource? _watcherCts;
    private bool _disposed;

    public event EventHandler<RemovableDriveEventArgs>? CardInserted;
    public event EventHandler<string>? CardRemoved;

    public void StartWatching()
    {
        if (_watcherCts != null) return;
        _watcherCts = new CancellationTokenSource();

        // Ghi nhận các ổ hiện có lúc mới khởi động để chỉ báo khi có thẻ mới cắm vào
        // hoặc nếu đã có thẻ chứa ảnh DCIM đang cắm sẵn thì báo luôn sau 1 giây
        _ = Task.Run(() => MonitorMountedVolumesLoopAsync(_watcherCts.Token));
    }

    private async Task MonitorMountedVolumesLoopAsync(CancellationToken ct)
    {
        bool initialPass = true;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                List<string> currentCandidateRoots = GetCandidateRemovableRoots();

                List<string> newlyAdded;
                List<string> removed;

                lock (_knownLock)
                {
                    newlyAdded = currentCandidateRoots
                        .Where(r => !_knownMountedCards.Contains(r))
                        .ToList();

                    removed = _knownMountedCards
                        .Where(r => !currentCandidateRoots.Contains(r, StringComparer.OrdinalIgnoreCase))
                        .ToList();

                    foreach (string add in newlyAdded)
                        _knownMountedCards.Add(add);

                    foreach (string rem in removed)
                        _knownMountedCards.Remove(rem);
                }

                foreach (string rem in removed)
                {
                    CardRemoved?.Invoke(this, rem);
                }

                foreach (string addedRoot in newlyAdded)
                {
                    // Nếu mới cắm thẻ vào (hoặc thẻ có thư mục DCIM lúc vừa mở app)
                    _ = InspectAndNotifyCardAsync(addedRoot, isInitialStartup: initialPass, ct);
                }

                initialPass = false;
            }
            catch
            {
            }

            try
            {
                await Task.Delay(1800, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static List<string> GetCandidateRemovableRoots()
    {
        var roots = new List<string>();

        if (OperatingSystem.IsMacOS())
        {
            const string volumesDir = "/Volumes";
            if (Directory.Exists(volumesDir))
            {
                try
                {
                    foreach (string dir in Directory.EnumerateDirectories(volumesDir))
                    {
                        string volName = Path.GetFileName(dir);
                        if (string.IsNullOrWhiteSpace(volName) ||
                            volName.StartsWith('.') ||
                            IgnoredMacVolumes.Contains(volName))
                        {
                            continue;
                        }

                        // Bỏ qua symlink trỏ về "/" (ổ hệ thống của macOS)
                        try
                        {
                            var dirInfo = new DirectoryInfo(dir);
                            if (dirInfo.LinkTarget == "/")
                                continue;
                        }
                        catch { }

                        roots.Add(dir);
                    }
                }
                catch { }
            }
        }
        else
        {
            try
            {
                foreach (var drive in DriveInfo.GetDrives())
                {
                    if (drive.IsReady && drive.DriveType == DriveType.Removable)
                    {
                        roots.Add(drive.RootDirectory.FullName);
                    }
                }
            }
            catch { }
        }

        return roots;
    }

    private async Task InspectAndNotifyCardAsync(string volumeRoot, bool isInitialStartup, CancellationToken ct)
    {
        try
        {
            if (!isInitialStartup)
            {
                // Chờ macOS mount ổn định hệ thống file trên thẻ nhớ
                await Task.Delay(700, ct).ConfigureAwait(false);
            }

            if (!Directory.Exists(volumeRoot)) return;

            // Nếu là lần đầu mở app trên macOS, chỉ tự động chọn những Volume thực sự giống thẻ nhớ máy ảnh (có DCIM, PRIVATE, MP_ROOT)
            if (isInitialStartup && OperatingSystem.IsMacOS())
            {
                bool hasCameraStructure =
                    Directory.Exists(Path.Combine(volumeRoot, "DCIM")) ||
                    Directory.Exists(Path.Combine(volumeRoot, "PRIVATE")) ||
                    Directory.Exists(Path.Combine(volumeRoot, "MP_ROOT"));
                if (!hasCameraStructure)
                {
                    return;
                }
            }

            IReadOnlyList<string> mediaFiles = await Task.Run(() => ScanMediaFiles(volumeRoot, ct), ct).ConfigureAwait(false);
            if (mediaFiles.Count == 0)
            {
                return;
            }

            string volumeLabel = Path.GetFileName(volumeRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            long totalSize = 0;

            try
            {
                var (_, total, _) = CopyEngineService.GetDriveSpaceInfo(volumeRoot);
                totalSize = total;
            }
            catch { }

            CardInserted?.Invoke(this, new RemovableDriveEventArgs
            {
                DriveRoot = volumeRoot,
                VolumeLabel = string.IsNullOrWhiteSpace(volumeLabel) ? volumeRoot : volumeLabel,
                TotalSizeBytes = totalSize,
                MediaFiles = mediaFiles
            });
        }
        catch
        {
        }
    }

    /// <summary>
    /// Quét đệ quy ổ đĩa hoặc thư mục, chỉ lọc các file Media hợp lệ và bỏ qua file rác máy ảnh/hệ thống macOS.
    /// </summary>
    public List<string> ScanMediaFiles(string rootPath, CancellationToken cancellationToken = default)
    {
        var results = new List<string>();
        if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
        {
            return results;
        }

        var pendingDirs = new Stack<string>();
        pendingDirs.Push(rootPath);

        while (pendingDirs.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string currentDir = pendingDirs.Pop();

            try
            {
                foreach (string subDir in Directory.EnumerateDirectories(currentDir))
                {
                    string dirName = Path.GetFileName(subDir);
                    if (string.IsNullOrEmpty(dirName) ||
                        dirName.StartsWith('.') ||
                        IgnoredDirectoryNames.Contains(dirName))
                    {
                        continue;
                    }

                    try
                    {
                        var attr = File.GetAttributes(subDir);
                        if ((attr & FileAttributes.System) != 0)
                        {
                            continue;
                        }
                    }
                    catch { }

                    pendingDirs.Push(subDir);
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }

            try
            {
                foreach (string file in Directory.EnumerateFiles(currentDir))
                {
                    string fileName = Path.GetFileName(file);
                    // Bỏ qua các file resource fork của macOS (._IMG_0001.CR3, .DS_Store) hoặc file ẩn
                    if (string.IsNullOrEmpty(fileName) || fileName.StartsWith("._") || fileName.StartsWith('.'))
                    {
                        continue;
                    }

                    string ext = Path.GetExtension(fileName);
                    if (!SupportedExtensions.Contains(ext))
                    {
                        continue;
                    }

                    try
                    {
                        var info = new FileInfo(file);
                        if (info.Length == 0 || (info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                        {
                            continue;
                        }
                    }
                    catch
                    {
                        continue;
                    }

                    results.Add(file);
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }

        results.Sort(StringComparer.OrdinalIgnoreCase);
        return results;
    }

    /// <summary>
    /// Ngắt kết nối an toàn (Auto-Eject) thẻ nhớ trên macOS bằng `diskutil eject` (hoặc PowerShell trên Windows).
    /// </summary>
    public async Task<(bool Success, string Message)> EjectRemovableDriveAsync(string driveOrFolderPath)
    {
        if (string.IsNullOrWhiteSpace(driveOrFolderPath))
        {
            return (false, "Đường dẫn thẻ nhớ rỗng.");
        }

        return await Task.Run(() =>
        {
            if (OperatingSystem.IsMacOS())
            {
                string? volumePath = ResolveMacVolumeRoot(driveOrFolderPath);
                if (string.IsNullOrWhiteSpace(volumePath))
                {
                    return (false, "Đường dẫn nguồn không nằm trên thẻ nhớ /Volumes (bỏ qua Auto-Eject).");
                }

                string volName = Path.GetFileName(volumePath);
                if (IgnoredMacVolumes.Contains(volName))
                {
                    return (false, $"Bỏ qua Eject ổ hệ thống macOS ({volName}).");
                }

                if (RunMacDiskUtil("eject", volumePath) || RunMacDiskUtil("unmount", volumePath))
                {
                    return (true, $"Đã ngắt kết nối an toàn thẻ nhớ ({volName}) trên macOS qua diskutil eject.");
                }

                return (false, $"Không thể tự động Eject thẻ '{volName}'. Có thể Finder hoặc ứng dụng khác đang mở file trên thẻ.");
            }
            else
            {
                string? root = Path.GetPathRoot(driveOrFolderPath);
                if (string.IsNullOrWhiteSpace(root) || root.Length < 2)
                {
                    return (false, "Không xác định được ổ đĩa thẻ nhớ.");
                }

                string driveLetter = root.Substring(0, 2);
                try
                {
                    var di = new DriveInfo(root);
                    if (di.DriveType != DriveType.Removable)
                    {
                        return (false, $"Ổ đĩa {driveLetter} không phải là thẻ nhớ rời (bỏ qua Auto-Eject).");
                    }
                }
                catch { }

                try
                {
                    string script = $"(New-Object -ComObject Shell.Application).Namespace(17).ParseName('{driveLetter}').InvokeVerb('Eject')";
                    var psi = new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = $"-NoProfile -NonInteractive -WindowStyle Hidden -Command \"{script}\"",
                        CreateNoWindow = true,
                        UseShellExecute = false
                    };
                    using var proc = Process.Start(psi);
                    proc?.WaitForExit(4000);
                    if (proc?.ExitCode == 0)
                    {
                        return (true, $"Đã ngắt kết nối an toàn thẻ nhớ ({driveLetter}).");
                    }
                }
                catch { }

                return (false, $"Không thể tự động Eject ổ {driveLetter}.");
            }
        }).ConfigureAwait(false);
    }

    public static string? ResolveMacVolumeRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string normalized = path.Replace('\\', '/');
        if (!normalized.StartsWith("/Volumes/", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string[] parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && string.Equals(parts[0], "Volumes", StringComparison.OrdinalIgnoreCase))
        {
            return "/Volumes/" + parts[1];
        }

        return null;
    }

    private static bool RunMacDiskUtil(string verb, string volumePath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "/usr/sbin/diskutil",
                Arguments = $"{verb} \"{volumePath}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(6000);
            return proc?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _watcherCts?.Cancel();
        _watcherCts?.Dispose();
        _disposed = true;
    }
}
