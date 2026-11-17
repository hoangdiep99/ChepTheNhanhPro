using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using PhotoIngestTool.Models;
using Standart.Hash.xxHash;

namespace PhotoIngestTool.Services;

/// <summary>
/// Module 3 (Nâng cấp Pro):
/// - Luồng sao chép bất đồng bộ (FileStream Buffer 8MB) + Dual-Backup đồng thời.
/// - Tính toán và đọc ngược kiểm tra toàn vẹn xxHash64.
/// - Chế độ Incremental Ingest (bỏ qua file đã tồn tại khớp kích thước khi đổ bồi thêm thẻ).
/// - Đổi tên file thông minh theo mẫu (Thợ chụp / Ngày chụp / Khách hàng).
/// - Hỗ trợ Tạm dừng (Pause) / Tiếp tục (Resume) / Hủy (Cancel) ngay giữa chừng và tính ETA.
/// - Kiểm tra dung lượng trống ổ cứng đích trước khi đổ.
/// </summary>
public sealed class CopyEngineService : IDisposable
{
    public const int OptimalBufferSize = 8 * 1024 * 1024;
    public const int MaxRetryCount = 3;

    // Win32 API ngăn Windows tự động Sleep/Standby khi đang đổ thẻ dung lượng lớn
    private const uint ES_CONTINUOUS = 0x80000000;
    private const uint ES_SYSTEM_REQUIRED = 0x00000001;

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SetThreadExecutionState(uint esFlags);

    private readonly MetadataService _metadataService;
    private readonly DriveWatcherService _driveWatcherService;
    private readonly Channel<QueuedIngestWorkItem> _jobChannel;
    private readonly CancellationTokenSource _serviceCts = new();
    private CancellationTokenSource? _activeJobCts;
    private volatile TaskCompletionSource<bool>? _pauseGate;
    private int _pendingJobCount;
    private long _lastProgressReportTick;
    private bool _disposed;

    public event EventHandler<IngestProgressInfo>? ProgressChanged;
    public event EventHandler<IngestJobSummary>? JobCompleted;
    public event EventHandler<string>? LogMessageEmitted;

    private sealed record QueuedIngestWorkItem(
        IngestJobRequest Request,
        TaskCompletionSource<IngestJobSummary> CompletionSource,
        CancellationToken CancellationToken);

    public CopyEngineService(MetadataService metadataService, DriveWatcherService driveWatcherService)
    {
        _metadataService = metadataService ?? throw new ArgumentNullException(nameof(metadataService));
        _driveWatcherService = driveWatcherService ?? throw new ArgumentNullException(nameof(driveWatcherService));

        _jobChannel = Channel.CreateUnbounded<QueuedIngestWorkItem>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        _ = Task.Run(ProcessQueueLoopAsync);
    }

    public int PendingJobCount => Volatile.Read(ref _pendingJobCount);
    public bool IsPaused => _pauseGate != null;

    /// <summary>
    /// Tạm dừng hoặc tiếp tục luồng sao chép hiện tại ngay lập tức.
    /// </summary>
    public void SetPaused(bool pause)
    {
        if (pause)
        {
            if (_pauseGate == null)
            {
                _pauseGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                EmitLog("[Tạm dừng] Đã tạm dừng tiến trình sao chép theo yêu cầu.");
            }
        }
        else
        {
            var gate = Interlocked.Exchange(ref _pauseGate, null);
            if (gate != null)
            {
                gate.TrySetResult(true);
                EmitLog("[Tiếp tục] Đã tiếp tục tiến trình sao chép.");
            }
        }
    }

    /// <summary>
    /// Hủy Job đang chạy hiện tại.
    /// </summary>
    public void CancelActiveJob()
    {
        SetPaused(false);
        _activeJobCts?.Cancel();
        EmitLog("[Hủy Job] Đang hủy tiến trình đổ thẻ hiện tại...");
    }

    private async ValueTask WaitIfPausedAsync(CancellationToken cancellationToken)
    {
        var gate = _pauseGate;
        if (gate != null)
        {
            await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public Task<IngestJobSummary> EnqueueJobAsync(
        IngestJobRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var tcs = new TaskCompletionSource<IngestJobSummary>(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new QueuedIngestWorkItem(request, tcs, cancellationToken);

        int queuedNow = Interlocked.Increment(ref _pendingJobCount);
        EmitLog($"[Hàng đợi] Đã thêm thẻ '{request.SourceDriveOrFolder}' vào hàng đợi (Đang chờ: {queuedNow} job).");

        if (!_jobChannel.Writer.TryWrite(item))
        {
            Interlocked.Decrement(ref _pendingJobCount);
            tcs.TrySetException(new InvalidOperationException("Không thể thêm Job vào hàng đợi Ingest."));
        }

        return tcs.Task;
    }

    private async Task ProcessQueueLoopAsync()
    {
        try
        {
            await foreach (var workItem in _jobChannel.Reader.ReadAllAsync(_serviceCts.Token).ConfigureAwait(false))
            {
                _activeJobCts = CancellationTokenSource.CreateLinkedTokenSource(
                    _serviceCts.Token,
                    workItem.CancellationToken);

                Process? macCaffeinateProc = null;
                try
                {
                    if (OperatingSystem.IsWindows())
                    {
                        try { SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED); } catch { }
                    }
                    else if (OperatingSystem.IsMacOS())
                    {
                        try
                        {
                            macCaffeinateProc = Process.Start(new ProcessStartInfo
                            {
                                FileName = "/usr/bin/caffeinate",
                                Arguments = $"-i -w {Environment.ProcessId}",
                                CreateNoWindow = true,
                                UseShellExecute = false
                            });
                        }
                        catch { }
                    }

                    IngestJobSummary summary = await ExecuteJobInternalAsync(workItem.Request, _activeJobCts.Token)
                        .ConfigureAwait(false);

                    workItem.CompletionSource.TrySetResult(summary);
                    JobCompleted?.Invoke(this, summary);
                }
                catch (OperationCanceledException oce)
                {
                    workItem.CompletionSource.TrySetCanceled(oce.CancellationToken);
                }
                catch (Exception ex)
                {
                    workItem.CompletionSource.TrySetException(ex);
                }
                finally
                {
                    if (OperatingSystem.IsWindows())
                    {
                        try { SetThreadExecutionState(ES_CONTINUOUS); } catch { }
                    }
                    try
                    {
                        if (macCaffeinateProc is { HasExited: false })
                            macCaffeinateProc.Kill();
                        macCaffeinateProc?.Dispose();
                    }
                    catch { }

                    _activeJobCts?.Dispose();
                    _activeJobCts = null;
                    Interlocked.Decrement(ref _pendingJobCount);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public static string BuildJobRootDirectory(string rootFolder, IngestJobRequest request)
    {
        string datePrefix = request.ShootDate.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture);

        string safeClient = SanitizeFolderSegment(string.IsNullOrWhiteSpace(request.ClientName) ? "Khách Hàng" : request.ClientName);
        string safeJobType = SanitizeFolderSegment(string.IsNullOrWhiteSpace(request.JobType) ? "Job" : request.JobType);

        string jobFolderName = $"{datePrefix} - {safeClient} - {safeJobType}";
        return Path.Combine(rootFolder, jobFolderName);
    }

    public static string BuildDestinationMediaDirectory(string rootFolder, IngestJobRequest request)
    {
        string jobRoot = BuildJobRootDirectory(rootFolder, request);
        if (!request.IsMultiCamera)
        {
            return jobRoot;
        }

        string safePhotographer = SanitizeFolderSegment(
            string.IsNullOrWhiteSpace(request.PhotographerName) ? "ThoChup" : request.PhotographerName);
        string safeModel = SanitizeFolderSegment(
            string.IsNullOrWhiteSpace(request.CameraModel) ? "Camera" : request.CameraModel);

        string camSubFolder = $"Cam_{safePhotographer}_{safeModel}";
        return Path.Combine(jobRoot, camSubFolder);
    }

    /// <summary>
    /// Phân loại thư mục con (RAW / JPG / Video) theo đuôi file.
    /// </summary>
    public static string GetMediaSubfolder(string filePath)
    {
        string ext = Path.GetExtension(filePath);
        if (string.Equals(ext, ".MP4", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(ext, ".MOV", StringComparison.OrdinalIgnoreCase))
        {
            return "Video";
        }

        if (string.Equals(ext, ".JPG", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(ext, ".JPEG", StringComparison.OrdinalIgnoreCase))
        {
            return "JPG";
        }

        return "RAW";
    }

    /// <summary>
    /// Tạo tên file đích theo mẫu đổi tên (nếu bật EnableCustomRename) hoặc giữ nguyên tên gốc.
    /// </summary>
    public static string FormatDestinationFileName(string originalFileName, IngestJobRequest request)
    {
        if (!request.EnableCustomRename || string.IsNullOrWhiteSpace(request.RenameTemplate))
        {
            return originalFileName;
        }

        string template = request.RenameTemplate.Trim();
        if (!template.Contains("{Original}", StringComparison.OrdinalIgnoreCase))
        {
            template = $"{template.TrimEnd('_', '-', ' ')}_{{Original}}";
        }

        string ext = Path.GetExtension(originalFileName);
        string origNameOnly = Path.GetFileNameWithoutExtension(originalFileName);
        string safePhotographer = SanitizeFolderSegment(
            string.IsNullOrWhiteSpace(request.PhotographerName) ? "Photographer" : request.PhotographerName)
            .Replace(" ", "");
        string safeClient = SanitizeFolderSegment(
            string.IsNullOrWhiteSpace(request.ClientName) ? "Client" : request.ClientName)
            .Replace(" ", "");
        string safeModel = SanitizeFolderSegment(
            string.IsNullOrWhiteSpace(request.CameraModel) ? "Cam" : request.CameraModel)
            .Replace(" ", "");
        string dateStr = request.ShootDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

        string formatted = template
            .Replace("{Photographer}", safePhotographer, StringComparison.OrdinalIgnoreCase)
            .Replace("{Client}", safeClient, StringComparison.OrdinalIgnoreCase)
            .Replace("{Model}", safeModel, StringComparison.OrdinalIgnoreCase)
            .Replace("{Date}", dateStr, StringComparison.OrdinalIgnoreCase)
            .Replace("{Original}", origNameOnly, StringComparison.OrdinalIgnoreCase);

        formatted = SanitizeFolderSegment(formatted);
        if (string.IsNullOrWhiteSpace(formatted))
        {
            return originalFileName;
        }

        return formatted + ext;
    }

    public static string SanitizeFolderSegment(string input)
    {
        char[] invalidChars = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(input.Length);
        foreach (char c in input.Trim())
        {
            if (Array.IndexOf(invalidChars, c) >= 0 || c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar)
            {
                sb.Append('_');
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString().Trim();
    }

    /// <summary>
    /// Lấy thông tin dung lượng trống của ổ cứng hoặc thẻ nhớ chứa đường dẫn bất kỳ (hỗ trợ cả Windows Drive và macOS /Volumes).
    /// </summary>
    public static (long FreeBytes, long TotalBytes, string DriveName) GetDriveSpaceInfo(string folderPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(folderPath))
                return (0, 0, string.Empty);

            string fullPath = Path.GetFullPath(folderPath);

            if (OperatingSystem.IsMacOS())
            {
                string? macVol = DriveWatcherService.ResolveMacVolumeRoot(fullPath);
                if (!string.IsNullOrWhiteSpace(macVol) && Directory.Exists(macVol))
                {
                    var volDrive = new DriveInfo(macVol);
                    if (volDrive.IsReady)
                    {
                        string label = Path.GetFileName(macVol);
                        return (volDrive.AvailableFreeSpace, volDrive.TotalSize, label);
                    }
                }

                // Tìm DriveInfo có RootDirectory khớp dài nhất với fullPath
                DriveInfo? bestMatch = null;
                int bestLen = -1;
                foreach (var d in DriveInfo.GetDrives())
                {
                    string r = d.RootDirectory.FullName;
                    if (fullPath.StartsWith(r, StringComparison.OrdinalIgnoreCase) && r.Length > bestLen)
                    {
                        bestMatch = d;
                        bestLen = r.Length;
                    }
                }

                if (bestMatch != null && bestMatch.IsReady)
                {
                    string display = bestMatch.RootDirectory.FullName == "/"
                        ? "Macintosh HD"
                        : Path.GetFileName(bestMatch.RootDirectory.FullName.TrimEnd('/'));
                    return (bestMatch.AvailableFreeSpace, bestMatch.TotalSize, display);
                }
            }

            string? root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrWhiteSpace(root))
                return (0, 0, string.Empty);

            var di = new DriveInfo(root);
            if (!di.IsReady)
                return (0, 0, root);

            return (di.AvailableFreeSpace, di.TotalSize, root);
        }
        catch
        {
            return (0, 0, string.Empty);
        }
    }

    private async Task<IngestJobSummary> ExecuteJobInternalAsync(
        IngestJobRequest request,
        CancellationToken cancellationToken)
    {
        var summary = new IngestJobSummary
        {
            Request = request,
            StartTime = DateTime.Now
        };

        string primaryJobRoot = BuildJobRootDirectory(request.PrimaryRootFolder, request);
        string primaryMediaDir = BuildDestinationMediaDirectory(request.PrimaryRootFolder, request);
        Directory.CreateDirectory(primaryMediaDir);
        summary.PrimaryJobDirectory = primaryJobRoot;

        string? backupJobRoot = null;
        string? backupMediaDir = null;
        bool hasDualBackup = request.EnableDualBackup && !string.IsNullOrWhiteSpace(request.BackupRootFolder);
        if (hasDualBackup)
        {
            backupJobRoot = BuildJobRootDirectory(request.BackupRootFolder!, request);
            backupMediaDir = BuildDestinationMediaDirectory(request.BackupRootFolder!, request);
            Directory.CreateDirectory(backupMediaDir);
            summary.BackupJobDirectory = backupJobRoot;
        }

        IReadOnlyList<string> sourceFiles = request.MediaFiles.Count > 0
            ? request.MediaFiles
            : _driveWatcherService.ScanMediaFiles(request.SourceDriveOrFolder, cancellationToken);

        long totalBytes = 0;
        foreach (string f in sourceFiles)
        {
            try { totalBytes += new FileInfo(f).Length; } catch { }
        }

        // Kiểm tra dung lượng trống trên ổ cứng đích trước khi đổ
        var (primaryFree, _, primaryDrive) = GetDriveSpaceInfo(primaryMediaDir);
        if (primaryFree > 0 && primaryFree < totalBytes)
        {
            throw new IOException(
                $"Ổ cứng chính ({primaryDrive}) không đủ dung lượng trống! Cần {FormatBytes(totalBytes)}, còn trống {FormatBytes(primaryFree)}.");
        }

        if (hasDualBackup && backupMediaDir != null)
        {
            var (backupFree, _, backupDrive) = GetDriveSpaceInfo(backupMediaDir);
            if (backupFree > 0 && backupFree < totalBytes)
            {
                throw new IOException(
                    $"Ổ cứng Backup ({backupDrive}) không đủ dung lượng trống! Cần {FormatBytes(totalBytes)}, còn trống {FormatBytes(backupFree)}.");
            }
        }

        long transferredBytesSoFar = 0;
        var stopwatch = Stopwatch.StartNew();

        string splitNote = request.SplitByFileType ? " (Tự tách RAW / JPG / Video)" : string.Empty;
        EmitLog($"[Bắt đầu] Đổ {sourceFiles.Count} file ({FormatBytes(totalBytes)}) -> {primaryMediaDir}{splitNote}");

        for (int index = 0; index < sourceFiles.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await WaitIfPausedAsync(cancellationToken).ConfigureAwait(false);

            string srcFile = sourceFiles[index];
            string origFileName = Path.GetFileName(srcFile);
            string destFileName = FormatDestinationFileName(origFileName, request);

            long currentFileLength = 0;
            try { currentFileLength = new FileInfo(srcFile).Length; } catch { }

            string subFolder = request.SplitByFileType ? GetMediaSubfolder(srcFile) : string.Empty;
            string effectivePrimaryDir = string.IsNullOrEmpty(subFolder)
                ? primaryMediaDir
                : Path.Combine(primaryMediaDir, subFolder);
            Directory.CreateDirectory(effectivePrimaryDir);

            string? effectiveBackupDir = null;
            if (hasDualBackup && backupMediaDir != null)
            {
                effectiveBackupDir = string.IsNullOrEmpty(subFolder)
                    ? backupMediaDir
                    : Path.Combine(backupMediaDir, subFolder);
                Directory.CreateDirectory(effectiveBackupDir);
            }

            string primaryCandidate = Path.Combine(effectivePrimaryDir, destFileName);
            string? backupCandidate = effectiveBackupDir != null
                ? Path.Combine(effectiveBackupDir, destFileName)
                : null;

            // Kiểm tra Incremental Skip: nếu file đã tồn tại ở đích và khớp chính xác kích thước byte
            if (request.IncrementalSkipExisting &&
                IsExistingFileIdenticalSize(primaryCandidate, currentFileLength) &&
                (!hasDualBackup || (backupCandidate != null && IsExistingFileIdenticalSize(backupCandidate, currentFileLength))))
            {
                long updatedBytes = Interlocked.Add(ref transferredBytesSoFar, currentFileLength);
                summary.SkippedExistingCount++;
                summary.VerifiedCount++;
                summary.TotalBytesVerified += currentFileLength;

                summary.FileResults.Add(new FileTransferResult
                {
                    SourceFilePath = srcFile,
                    FileName = origFileName,
                    DestinationFileName = destFileName,
                    FileSizeBytes = currentFileLength,
                    PrimaryDestPath = primaryCandidate,
                    BackupDestPath = backupCandidate,
                    IsVerified = true,
                    WasSkippedExisting = true
                });

                ReportProgress(BuildProgressSnapshot(
                    request.JobId,
                    destFileName,
                    index + 1,
                    sourceFiles.Count,
                    updatedBytes,
                    totalBytes,
                    stopwatch.Elapsed.TotalSeconds,
                    $"Bỏ qua file đã có sẵn: {destFileName}"));

                continue;
            }

            string primaryDestFile = ResolveUniqueDestinationPath(effectivePrimaryDir, destFileName, srcFile);
            string? backupDestFile = effectiveBackupDir != null
                ? ResolveUniqueDestinationPath(effectiveBackupDir, destFileName, srcFile)
                : null;

            var fileResult = await CopyAndVerifySingleFileWithRetryAsync(
                srcFile,
                destFileName,
                primaryDestFile,
                backupDestFile,
                request,
                bytesDelta =>
                {
                    long currentTotal = Interlocked.Add(ref transferredBytesSoFar, bytesDelta);
                    ReportProgress(BuildProgressSnapshot(
                        request.JobId,
                        destFileName,
                        index,
                        sourceFiles.Count,
                        currentTotal,
                        totalBytes,
                        stopwatch.Elapsed.TotalSeconds,
                        IsPaused ? $"[TẠM DỪNG] {destFileName}" : $"Đang chép & băm xxHash64: {destFileName}"));
                },
                cancellationToken).ConfigureAwait(false);

            summary.FileResults.Add(fileResult);

            if (fileResult.IsVerified)
            {
                summary.VerifiedCount++;
                summary.TotalBytesVerified += fileResult.FileSizeBytes;
            }
            else
            {
                summary.SkippedOrFailedCount++;
                EmitLog($"[CẢNH BÁO] Bỏ qua file lỗi sau {MaxRetryCount} lần thử: {origFileName} - {fileResult.ErrorMessage}");
            }

            ReportProgress(BuildProgressSnapshot(
                request.JobId,
                destFileName,
                index + 1,
                sourceFiles.Count,
                Interlocked.Read(ref transferredBytesSoFar),
                totalBytes,
                stopwatch.Elapsed.TotalSeconds,
                fileResult.IsVerified ? $"Đã xác thực: {destFileName}" : $"Lỗi (Đã bỏ qua): {destFileName}"));
        }

        summary.EndTime = DateTime.Now;

        string primaryReportPath = Path.Combine(primaryJobRoot, "ingest_report.txt");
        await WriteIngestReportAsync(primaryReportPath, summary, cancellationToken).ConfigureAwait(false);
        summary.ReportFilePath = primaryReportPath;

        if (hasDualBackup && !string.IsNullOrWhiteSpace(backupJobRoot))
        {
            string backupReportPath = Path.Combine(backupJobRoot, "ingest_report.txt");
            await WriteIngestReportAsync(backupReportPath, summary, cancellationToken).ConfigureAwait(false);
        }

        EmitLog($"[Hoàn tất] Xác thực: {summary.VerifiedCount}/{sourceFiles.Count} file (Bỏ qua trùng: {summary.SkippedExistingCount}). Báo cáo: {primaryReportPath}");

        if (request.AutoEjectAfterIngest && summary.SkippedOrFailedCount == 0)
        {
            var (ejected, ejectMsg) = await _driveWatcherService
                .EjectRemovableDriveAsync(request.SourceDriveOrFolder)
                .ConfigureAwait(false);
            summary.WasEjected = ejected;
            EmitLog($"[Auto-Eject] {ejectMsg}");
        }

        return summary;
    }

    private IngestProgressInfo BuildProgressSnapshot(
        Guid jobId,
        string currentFileName,
        int completedFiles,
        int totalFiles,
        long transferredBytes,
        long totalBytes,
        double elapsedSeconds,
        string phase)
    {
        double safeSec = Math.Max(0.15, elapsedSeconds);
        double speedMBps = (transferredBytes / (1024.0 * 1024.0)) / safeSec;
        long remainingBytes = Math.Max(0, totalBytes - transferredBytes);

        TimeSpan? eta = null;
        if (speedMBps > 0.1 && remainingBytes > 0)
        {
            double remainingSec = (remainingBytes / (1024.0 * 1024.0)) / speedMBps;
            if (remainingSec < 86400)
            {
                eta = TimeSpan.FromSeconds(remainingSec);
            }
        }

        return new IngestProgressInfo
        {
            JobId = jobId,
            CurrentFileName = currentFileName,
            CompletedFiles = completedFiles,
            TotalFiles = totalFiles,
            TransferredBytes = transferredBytes,
            TotalBytes = totalBytes,
            SpeedMegabytesPerSecond = speedMBps,
            EstimatedRemaining = eta,
            Phase = phase,
            PendingJobsInQueue = PendingJobCount,
            IsPaused = IsPaused
        };
    }

    private static bool IsExistingFileIdenticalSize(string filePath, long expectedLength)
    {
        try
        {
            var fi = new FileInfo(filePath);
            return fi.Exists && fi.Length == expectedLength;
        }
        catch
        {
            return false;
        }
    }

    private async Task<FileTransferResult> CopyAndVerifySingleFileWithRetryAsync(
        string sourceFilePath,
        string destFileName,
        string primaryDestPath,
        string? backupDestPath,
        IngestJobRequest request,
        Action<long> onBytesCopied,
        CancellationToken cancellationToken)
    {
        var result = new FileTransferResult
        {
            SourceFilePath = sourceFilePath,
            FileName = Path.GetFileName(sourceFilePath),
            DestinationFileName = destFileName,
            PrimaryDestPath = primaryDestPath,
            BackupDestPath = backupDestPath
        };

        for (int attempt = 1; attempt <= MaxRetryCount; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.RetryAttempts = attempt;
            long bytesInCurrentAttempt = 0;

            try
            {
                var sourceInfo = new FileInfo(sourceFilePath);
                result.FileSizeBytes = sourceInfo.Length;

                await using (var sourceStream = new FileStream(
                    sourceFilePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    OptimalBufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan))
                await using (var primaryWriteStream = new FileStream(
                    primaryDestPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    OptimalBufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan))
                await using (var backupWriteStream = !string.IsNullOrWhiteSpace(backupDestPath)
                    ? new FileStream(
                        backupDestPath,
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.None,
                        OptimalBufferSize,
                        FileOptions.Asynchronous | FileOptions.SequentialScan)
                    : null)
                {
                    await using var teeStream = new DualCopyTeeReadStream(
                        sourceStream,
                        primaryWriteStream,
                        backupWriteStream,
                        WaitIfPausedAsync,
                        bytesRead =>
                        {
                            bytesInCurrentAttempt += bytesRead;
                            onBytesCopied(bytesRead);
                        },
                        request.EnableThermalGuard);

                    result.SourceHash = await xxHash64.ComputeHashAsync(
                        teeStream,
                        bufferSize: OptimalBufferSize,
                        seed: 0,
                        cancellationToken: cancellationToken).ConfigureAwait(false);

                    await primaryWriteStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    if (backupWriteStream != null)
                    {
                        await backupWriteStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    }
                }

                PreserveFileTimestamps(sourceFilePath, primaryDestPath);
                if (!string.IsNullOrWhiteSpace(backupDestPath))
                {
                    PreserveFileTimestamps(sourceFilePath, backupDestPath);
                }

                await using (var primaryReadBack = new FileStream(
                    primaryDestPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    OptimalBufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    result.PrimaryDestHash = await xxHash64.ComputeHashAsync(
                        primaryReadBack,
                        bufferSize: OptimalBufferSize,
                        seed: 0,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                }

                if (result.PrimaryDestHash != result.SourceHash)
                {
                    throw new IOException(
                        $"Lệch Checksum xxHash64 trên ổ chính! Nguồn={result.SourceHashHex}, Đích={result.PrimaryHashHex}");
                }

                if (!string.IsNullOrWhiteSpace(backupDestPath))
                {
                    await using var backupReadBack = new FileStream(
                        backupDestPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        OptimalBufferSize,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);

                    ulong backupHash = await xxHash64.ComputeHashAsync(
                        backupReadBack,
                        bufferSize: OptimalBufferSize,
                        seed: 0,
                        cancellationToken: cancellationToken).ConfigureAwait(false);

                    result.BackupDestHash = backupHash;
                    if (backupHash != result.SourceHash)
                    {
                        throw new IOException(
                            $"Lệch Checksum xxHash64 trên ổ Backup! Nguồn={result.SourceHashHex}, Backup={result.BackupHashHex}");
                    }
                }

                if (request.TimeOffset != TimeSpan.Zero)
                {
                    await _metadataService.CreateTimeSyncSidecarXmpAsync(
                        sourceFilePath,
                        primaryDestPath,
                        request.TimeOffset,
                        request.PhotographerName,
                        request.CameraSerialNumber,
                        cancellationToken).ConfigureAwait(false);

                    if (!string.IsNullOrWhiteSpace(backupDestPath))
                    {
                        await _metadataService.CreateTimeSyncSidecarXmpAsync(
                            sourceFilePath,
                            backupDestPath,
                            request.TimeOffset,
                            request.PhotographerName,
                            request.CameraSerialNumber,
                            cancellationToken).ConfigureAwait(false);
                    }

                    result.SidecarGenerated = true;
                }

                result.IsVerified = true;
                result.ErrorMessage = null;

                if (request.EnableThermalGuard && result.FileSizeBytes > 20 * 1024 * 1024)
                {
                    await Task.Delay(20, cancellationToken).ConfigureAwait(false);
                }

                return result;
            }
            catch (OperationCanceledException)
            {
                SafeDeleteFile(primaryDestPath);
                if (!string.IsNullOrWhiteSpace(backupDestPath))
                {
                    SafeDeleteFile(backupDestPath);
                }
                throw;
            }
            catch (Exception ex)
            {
                if (bytesInCurrentAttempt > 0)
                {
                    onBytesCopied(-bytesInCurrentAttempt);
                }

                result.IsVerified = false;
                result.ErrorMessage = $"Lần thử {attempt}/{MaxRetryCount} thất bại: {ex.Message}";
                EmitLog($"[Retry {attempt}/{MaxRetryCount}] Lỗi tại file {result.FileName}: {ex.Message}");

                SafeDeleteFile(primaryDestPath);
                if (!string.IsNullOrWhiteSpace(backupDestPath))
                {
                    SafeDeleteFile(backupDestPath);
                }

                if (attempt < MaxRetryCount)
                {
                    await Task.Delay(400 * attempt, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        return result;
    }

    private static async Task WriteIngestReportAsync(
        string reportFilePath,
        IngestJobSummary summary,
        CancellationToken cancellationToken)
    {
        var req = summary.Request;
        var sb = new StringBuilder(8192);

        sb.AppendLine("==========================================================================================");
        sb.AppendLine("                       CHÉP THẺ NHANH PRO - BÁO CÁO ĐỔ THẺ (INGEST LOG)                   ");
        sb.AppendLine("==========================================================================================");
        sb.AppendLine($"Mã phiên (Job ID)       : {req.JobId}");
        sb.AppendLine($"Thời gian bắt đầu       : {summary.StartTime:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Thời gian kết thúc      : {summary.EndTime:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Tổng thời gian xử lý    : {summary.Duration:hh\\:mm\\:ss\\.fff}");
        sb.AppendLine($"Tốc độ trung bình       : {summary.AverageSpeedMBps:F2} MB/s");
        sb.AppendLine("------------------------------------------------------------------------------------------");
        sb.AppendLine("1. THÔNG TIN KHÁCH HÀNG & CẤU TRÚC LƯU TRỮ");
        sb.AppendLine($"   - Nguồn dữ liệu      : {req.SourceDriveOrFolder}");
        sb.AppendLine($"   - Thư mục ổ chính    : {summary.PrimaryJobDirectory}");
        sb.AppendLine($"   - Thư mục ổ Backup   : {(string.IsNullOrWhiteSpace(summary.BackupJobDirectory) ? "Không sử dụng" : summary.BackupJobDirectory)}");
        sb.AppendLine($"   - Ngày chụp (EXIF)   : {req.ShootDate:yyyy-MM-dd}");
        sb.AppendLine($"   - Tên khách hàng     : {req.ClientName}");
        sb.AppendLine($"   - Loại Job           : {req.JobType}");
        sb.AppendLine($"   - Chế độ nhiều máy   : {(req.IsMultiCamera ? "CÓ (Tạo thư mục con theo máy)" : "KHÔNG (Đổ thẳng vào thư mục Job)")}");
        sb.AppendLine($"   - Đổi tên file       : {(req.EnableCustomRename ? $"CÓ ({req.RenameTemplate})" : "KHÔNG (Giữ nguyên tên gốc)")}");
        sb.AppendLine("------------------------------------------------------------------------------------------");
        sb.AppendLine("2. THÔNG TIN MÁY ẢNH, THỢ CHỤP & ĐỒNG BỘ THỜI GIAN (TIME SYNC)");
        sb.AppendLine($"   - Thợ phụ trách      : {(string.IsNullOrWhiteSpace(req.PhotographerName) ? "N/A" : req.PhotographerName)}");
        sb.AppendLine($"   - Hãng / Model máy   : {req.CameraMake} / {req.CameraModel}");
        sb.AppendLine($"   - Body Serial Number : {(string.IsNullOrWhiteSpace(req.CameraSerialNumber) ? "N/A" : req.CameraSerialNumber)}");
        sb.AppendLine($"   - Độ lệch giờ Offset : {FormatTimeSpanOffset(req.TimeOffset)} {(req.TimeOffset != TimeSpan.Zero ? "(Đã sinh Sidecar .xmp, giữ nguyên RAW gốc)" : "(Không bù giờ)")}");
        sb.AppendLine("------------------------------------------------------------------------------------------");
        sb.AppendLine("3. TỔNG KẾT TOÀN VẸN DỮ LIỆU (xxHash64 CHECKSUM)");
        sb.AppendLine($"   - Tổng số file quét  : {summary.FileResults.Count}");
        sb.AppendLine($"   - Xác thực 100% (OK) : {summary.VerifiedCount} (Chép mới: {summary.VerifiedCount - summary.SkippedExistingCount}, Bỏ qua trùng: {summary.SkippedExistingCount})");
        sb.AppendLine($"   - Lỗi / Bỏ qua (Skip): {summary.SkippedOrFailedCount}");
        sb.AppendLine($"   - Tổng dung lượng OK : {FormatBytes(summary.TotalBytesVerified)} ({summary.TotalBytesVerified:N0} bytes)");
        sb.AppendLine("------------------------------------------------------------------------------------------");
        sb.AppendLine("4. CHI TIẾT KIỂM TRA CHECKSUM TỪNG FILE");
        sb.AppendLine($"{"Tên File Đích",-30} | {"Dung lượng",12} | {"Source xxHash64",16} | {"Primary xxHash64",16} | {"Backup xxHash64",16} | {"XMP",3} | {"Trạng thái"}");
        sb.AppendLine(new string('-', 118));

        foreach (var item in summary.FileResults)
        {
            string status = item.WasSkippedExisting
                ? "OK (Skipped Existing)"
                : (item.IsVerified
                    ? (item.RetryAttempts > 1 ? $"OK (Retry {item.RetryAttempts})" : "OK (Verified)")
                    : $"SKIPPED ({item.ErrorMessage})");

            string displayName = string.IsNullOrEmpty(item.DestinationFileName) ? item.FileName : item.DestinationFileName;
            sb.AppendLine(
                $"{TruncateMiddle(displayName, 30),-30} | " +
                $"{FormatBytes(item.FileSizeBytes),12} | " +
                $"{item.SourceHashHex,16} | " +
                $"{item.PrimaryHashHex,16} | " +
                $"{item.BackupHashHex,16} | " +
                $"{(item.SidecarGenerated ? "Yes" : "No"),3} | " +
                $"{status}");
        }

        sb.AppendLine("==========================================================================================");

        Directory.CreateDirectory(Path.GetDirectoryName(reportFilePath)!);
        await File.WriteAllTextAsync(reportFilePath, sb.ToString(), Encoding.UTF8, cancellationToken)
            .ConfigureAwait(false);
    }

    private static string ResolveUniqueDestinationPath(string destDir, string fileName, string sourceFilePath)
    {
        string candidate = Path.Combine(destDir, fileName);
        if (!File.Exists(candidate))
        {
            return candidate;
        }

        try
        {
            if (new FileInfo(candidate).Length == new FileInfo(sourceFilePath).Length)
            {
                return candidate;
            }
        }
        catch { }

        string nameOnly = Path.GetFileNameWithoutExtension(fileName);
        string ext = Path.GetExtension(fileName);
        int counter = 1;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(destDir, $"{nameOnly}_{counter:D2}{ext}");
            counter++;
        }
        return candidate;
    }

    private static void PreserveFileTimestamps(string sourceFile, string destFile)
    {
        try
        {
            DateTime lastWrite = File.GetLastWriteTime(sourceFile);
            DateTime creation = File.GetCreationTime(sourceFile);
            File.SetCreationTime(destFile, creation);
            File.SetLastWriteTime(destFile, lastWrite);
        }
        catch { }
    }

    private static void SafeDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch { }
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double val = Math.Max(0, bytes);
        int unitIdx = 0;
        while (val >= 1024 && unitIdx < units.Length - 1)
        {
            val /= 1024;
            unitIdx++;
        }
        return $"{val:F2} {units[unitIdx]}";
    }

    private static string FormatTimeSpanOffset(TimeSpan ts)
    {
        string sign = ts < TimeSpan.Zero ? "-" : "+";
        TimeSpan abs = ts.Duration();
        return $"{sign}{abs.Hours:D2}:{abs.Minutes:D2}:{abs.Seconds:D2}";
    }

    private static string TruncateMiddle(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
            return text;

        int half = (maxLength - 3) / 2;
        return text.Substring(0, half) + "..." + text.Substring(text.Length - half);
    }

    private void ReportProgress(IngestProgressInfo info)
    {
        long now = Environment.TickCount64;
        bool isFinal = info.CompletedFiles >= info.TotalFiles && info.TotalFiles > 0;
        if (!isFinal && now - Interlocked.Read(ref _lastProgressReportTick) < 60)
        {
            return;
        }

        Interlocked.Exchange(ref _lastProgressReportTick, now);
        ProgressChanged?.Invoke(this, info);
    }

    private void EmitLog(string message)
    {
        LogMessageEmitted?.Invoke(this, $"[{DateTime.Now:HH:mm:ss}] {message}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _jobChannel.Writer.TryComplete();
        _serviceCts.Cancel();
        _serviceCts.Dispose();
        _disposed = true;
    }

    private sealed class DualCopyTeeReadStream : Stream
    {
        private readonly Stream _source;
        private readonly Stream _primaryDest;
        private readonly Stream? _backupDest;
        private readonly Func<CancellationToken, ValueTask> _waitIfPausedAsync;
        private readonly Action<int> _onChunkCopied;
        private readonly bool _enableThermalGuard;
        private int _bytesSinceLastThrottle;

        public DualCopyTeeReadStream(
            Stream source,
            Stream primaryDest,
            Stream? backupDest,
            Func<CancellationToken, ValueTask> waitIfPausedAsync,
            Action<int> onChunkCopied,
            bool enableThermalGuard = false)
        {
            _source = source;
            _primaryDest = primaryDest;
            _backupDest = backupDest;
            _waitIfPausedAsync = waitIfPausedAsync;
            _onChunkCopied = onChunkCopied;
            _enableThermalGuard = enableThermalGuard;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _source.Length;
        public override long Position
        {
            get => _source.Position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int bytesRead = _source.Read(buffer, offset, count);
            if (bytesRead > 0)
            {
                _primaryDest.Write(buffer, offset, bytesRead);
                _backupDest?.Write(buffer, offset, bytesRead);
                _onChunkCopied(bytesRead);
            }
            return bytesRead;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await _waitIfPausedAsync(cancellationToken).ConfigureAwait(false);

            int bytesRead = await _source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (bytesRead > 0)
            {
                ReadOnlyMemory<byte> slice = buffer.Slice(0, bytesRead);
                if (_backupDest != null)
                {
                    ValueTask primaryWrite = _primaryDest.WriteAsync(slice, cancellationToken);
                    ValueTask backupWrite = _backupDest.WriteAsync(slice, cancellationToken);
                    await primaryWrite.ConfigureAwait(false);
                    await backupWrite.ConfigureAwait(false);
                }
                else
                {
                    await _primaryDest.WriteAsync(slice, cancellationToken).ConfigureAwait(false);
                }

                _onChunkCopied(bytesRead);

                if (_enableThermalGuard)
                {
                    _bytesSinceLastThrottle += bytesRead;
                    if (_bytesSinceLastThrottle >= 32 * 1024 * 1024)
                    {
                        _bytesSinceLastThrottle = 0;
                        await Task.Delay(15, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            return bytesRead;
        }

        public override async Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            return await ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
        }

        public override void Flush() => _primaryDest.Flush();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
