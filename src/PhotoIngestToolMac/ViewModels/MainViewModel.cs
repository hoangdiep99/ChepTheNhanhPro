using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoIngestTool.Models;
using PhotoIngestTool.Services;

namespace PhotoIngestTool.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly DriveWatcherService _driveWatcherService;
    private readonly MetadataService _metadataService;
    private readonly CopyEngineService _copyEngineService;
    private readonly string _settingsFilePath;
    private readonly string _localExeSettingsPath;
    private readonly string _historyFilePath;
    private readonly string _localExeHistoryPath;
    private CancellationTokenSource? _activeScanCts;
    private IReadOnlyList<string> _scannedMediaFiles = Array.Empty<string>();
    private long _scannedTotalBytes;
    private bool _isLoadingSettings;

    public event EventHandler<IngestJobSummary>? JobFinishedNotification;
    public event EventHandler? CardInsertedAttentionRequested;

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".MP4", ".MOV"
    };

    public PhotoFilterViewModel FilterViewModel { get; } = new();

    [ObservableProperty]
    private int _selectedTabIndex;

    partial void OnSelectedTabIndexChanged(int value)
    {
        if (value == 1)
        {
            SyncDestinationToFilterTab(overrideIfNotEmpty: false);
        }
    }

    public void SyncDestinationToFilterTab(bool overrideIfNotEmpty = true)
    {
        string candidate = !string.IsNullOrWhiteSpace(PreviewDestinationPath) ? PreviewDestinationPath : PrimaryRootFolder;
        if (!string.IsNullOrWhiteSpace(candidate))
        {
            if (overrideIfNotEmpty || string.IsNullOrWhiteSpace(FilterViewModel.SourceDirectory))
            {
                FilterViewModel.SourceDirectory = candidate;
            }
            if (overrideIfNotEmpty || string.IsNullOrWhiteSpace(FilterViewModel.DestinationDirectory))
            {
                FilterViewModel.DestinationDirectory = candidate;
            }
        }
    }

    [ObservableProperty]
    private bool _isDarkMode = true;

    [ObservableProperty]
    private string _themeToggleText = "☀️ Sáng";

    public event Action<bool>? ThemeChanged;

    [RelayCommand]
    public void ToggleTheme()
    {
        IsDarkMode = !IsDarkMode;
        ThemeToggleText = IsDarkMode ? "☀️ Sáng" : "🌙 Tối";
        ThemeChanged?.Invoke(IsDarkMode);
        SaveUserSettings();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewDestinationPath))]
    [NotifyPropertyChangedFor(nameof(SourceDriveCapacityText))]
    [NotifyPropertyChangedFor(nameof(SourceDriveUsedPercentage))]
    private string _sourcePath = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewDestinationPath))]
    [NotifyPropertyChangedFor(nameof(PrimaryDriveSpaceText))]
    [NotifyPropertyChangedFor(nameof(PrimaryDriveUsedPercentage))]
    [NotifyPropertyChangedFor(nameof(PrimaryDriveAfterCopyText))]
    private string _primaryRootFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        "PhotoIngestArchive");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewBackupPath))]
    [NotifyPropertyChangedFor(nameof(BackupDriveSpaceText))]
    private string _backupRootFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        "PhotoIngestBackup");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewBackupPath))]
    [NotifyPropertyChangedFor(nameof(BackupDriveSpaceText))]
    private bool _enableDualBackup;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewDestinationPath))]
    [NotifyPropertyChangedFor(nameof(PreviewBackupPath))]
    [NotifyPropertyChangedFor(nameof(PreviewRenamedFileText))]
    [NotifyPropertyChangedFor(nameof(ShootDateOffset))]
    private DateTime _shootDate = DateTime.Today;

    public DateTimeOffset? ShootDateOffset
    {
        get => new DateTimeOffset(ShootDate.Date);
        set
        {
            if (value.HasValue)
            {
                ShootDate = value.Value.DateTime.Date;
            }
        }
    }

    /// <summary>
    /// Luôn để trống mỗi lần mở app để thợ tự nhập tên khách mới.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewDestinationPath))]
    [NotifyPropertyChangedFor(nameof(PreviewBackupPath))]
    [NotifyPropertyChangedFor(nameof(PreviewRenamedFileText))]
    private string _clientName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewDestinationPath))]
    [NotifyPropertyChangedFor(nameof(PreviewBackupPath))]
    private string _jobType = "Phóng sự cưới";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewDestinationPath))]
    [NotifyPropertyChangedFor(nameof(PreviewBackupPath))]
    private bool _isMultiCamera;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewDestinationPath))]
    [NotifyPropertyChangedFor(nameof(PreviewBackupPath))]
    [NotifyPropertyChangedFor(nameof(PreviewRenamedFileText))]
    private string _photographerName = "Hoàng Điệp";

    [ObservableProperty]
    private string _cameraMake = "NIKON CORPORATION";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewDestinationPath))]
    [NotifyPropertyChangedFor(nameof(PreviewBackupPath))]
    [NotifyPropertyChangedFor(nameof(PreviewRenamedFileText))]
    private string _cameraModel = "NIKON Z5_2";

    [ObservableProperty]
    private string _cameraSerialNumber = "7609738";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimeOffsetSummary))]
    private int _offsetHours;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimeOffsetSummary))]
    private int _offsetMinutes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimeOffsetSummary))]
    private int _offsetSeconds;

    [ObservableProperty]
    private bool _incrementalSkipExisting = true;

    /// <summary>
    /// Tự động phân loại vào thư mục con RAW / JPG / Video (Mặc định tích sẵn).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewDestinationPath))]
    [NotifyPropertyChangedFor(nameof(PreviewBackupPath))]
    private bool _splitByFileType = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewRenamedFileText))]
    private bool _enableCustomRename;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewRenamedFileText))]
    private string _renameTemplate = "{Photographer}_{Original}";

    [ObservableProperty]
    private bool _autoEjectAfterIngest = true;

    [ObservableProperty]
    private bool _enableThermalGuard = true;

    [ObservableProperty]
    private bool _playSoundOnComplete = true;

    [ObservableProperty]
    private int _detectedFileCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SourceDriveCapacityText))]
    private string _detectedTotalSizeText = "0 MB";

    [ObservableProperty]
    private string _mediaBreakdownText = "RAW: 0 • JPG: 0 • VIDEO: 0";

    /// <summary>
    /// Thông báo/Cảnh báo số ngày chụp phát hiện trên thẻ nhớ.
    /// </summary>
    [ObservableProperty]
    private string _cardDatesStatusText = "Chưa quét ngày chụp trên thẻ";

    [ObservableProperty]
    private bool _hasMultipleShootDates;

    [ObservableProperty]
    private double _progressPercentage;

    [ObservableProperty]
    private string _currentPhaseText = "Sẵn sàng - Hãy cắm thẻ nhớ hoặc chọn thư mục nguồn.";

    [ObservableProperty]
    private string _transferSpeedText = "0.00 MB/s";

    [ObservableProperty]
    private string _etaText = "--:--";

    [ObservableProperty]
    private int _pendingJobsInQueue;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PauseButtonText))]
    private bool _isJobPaused;

    [ObservableProperty]
    private bool _isJobRunning;

    [ObservableProperty]
    private string _lastReportFilePath = string.Empty;

    [ObservableProperty]
    private bool _isStartupEnabled;

    /// <summary>
    /// Danh sách Loại Job bằng tiếng Việt gần gũi với thợ chụp ảnh Việt Nam.
    /// </summary>
    public ObservableCollection<string> JobTypes { get; } = new()
    {
        "Phóng sự cưới",
        "Cưới truyền thống",
        "Pre-Wedding",
        "Lễ ăn hỏi",
        "Sự kiện",
        "Hội khóa - Kỷ yếu",
        "Chân dung - Nàng thơ",
        "Gia đình - Sinh nhật",
        "Sản phẩm - Thương mại",
        "Quay phim - TVC"
    };

    public ObservableCollection<string> RenameTemplates { get; } = new()
    {
        "{Photographer}_{Original}",
        "{Date}_{Photographer}_{Original}",
        "{Client}_{Photographer}_{Original}",
        "{Photographer}_{Model}_{Original}"
    };

    public ObservableCollection<string> ActivityLogs { get; } = new();

    public ObservableCollection<IngestHistoryEntry> IngestHistory { get; } = new();

    /// <summary>
    /// Danh sách các nhóm ngày chụp trên thẻ để người dùng có thể lọc/chọn ngày cần copy.
    /// </summary>
    public ObservableCollection<CardShootDateItem> ShootDateGroups { get; } = new();

    public TimeSpan CurrentTimeOffset => new(OffsetHours, OffsetMinutes, OffsetSeconds);

    public string PauseButtonText => IsJobPaused ? "Tiếp Tục" : "Tạm Dừng";

    public string SourceDriveCapacityText
    {
        get
        {
            var (free, total, drive) = CopyEngineService.GetDriveSpaceInfo(SourcePath);
            if (total <= 0)
            {
                return $"Dung lượng ảnh: {DetectedTotalSizeText}";
            }

            long used = Math.Max(0, total - free);
            double usedPct = (double)used / total * 100.0;
            return $"Thẻ ({drive}) đã dùng: {CopyEngineService.FormatBytes(used)} / {CopyEngineService.FormatBytes(total)} ({usedPct:F0}%)";
        }
    }

    public double SourceDriveUsedPercentage
    {
        get
        {
            var (free, total, _) = CopyEngineService.GetDriveSpaceInfo(SourcePath);
            if (total <= 0) return 0;
            return Math.Clamp((double)(total - free) / total * 100.0, 0, 100);
        }
    }

    public string PrimaryDriveSpaceText
    {
        get
        {
            var (free, total, drive) = CopyEngineService.GetDriveSpaceInfo(PrimaryRootFolder);
            if (total <= 0) return "Chưa xác định dung lượng ổ đích";
            double freePct = (double)free / total * 100.0;
            return $"Ổ đích ({drive}) còn trống: {CopyEngineService.FormatBytes(free)} / {CopyEngineService.FormatBytes(total)} ({freePct:F0}%)";
        }
    }

    public double PrimaryDriveUsedPercentage
    {
        get
        {
            var (free, total, _) = CopyEngineService.GetDriveSpaceInfo(PrimaryRootFolder);
            if (total <= 0) return 0;
            return Math.Clamp((double)(total - free) / total * 100.0, 0, 100);
        }
    }

    public string PrimaryDriveAfterCopyText
    {
        get
        {
            var (free, total, _) = CopyEngineService.GetDriveSpaceInfo(PrimaryRootFolder);
            if (total <= 0) return string.Empty;
            if (_scannedTotalBytes <= 0) return "Sẵn sàng lưu trữ";

            long remainingAfter = free - _scannedTotalBytes;
            if (remainingAfter < 0)
            {
                return $"⚠ THIẾU {CopyEngineService.FormatBytes(-remainingAfter)}!";
            }
            return $"Đủ chỗ (Chép xong còn {CopyEngineService.FormatBytes(remainingAfter)})";
        }
    }

    public string BackupDriveSpaceText
    {
        get
        {
            if (!EnableDualBackup) return "Tắt Dual Backup";
            var (free, total, drive) = CopyEngineService.GetDriveSpaceInfo(BackupRootFolder);
            if (total <= 0) return "Chưa xác định ổ Backup";
            return $"Ổ Backup ({drive}) trống: {CopyEngineService.FormatBytes(free)} / {CopyEngineService.FormatBytes(total)}";
        }
    }

    public string PreviewRenamedFileText
    {
        get
        {
            string sampleOrig = _scannedMediaFiles.Count > 0
                ? Path.GetFileName(_scannedMediaFiles[0])
                : "DSC_0001.NEF";

            if (!EnableCustomRename)
            {
                return $"Giữ nguyên: {sampleOrig}";
            }

            string renamed = CopyEngineService.FormatDestinationFileName(sampleOrig, BuildCurrentJobRequest());
            return $"VD: {renamed}";
        }
    }

    public string TimeOffsetSummary
    {
        get
        {
            var ts = CurrentTimeOffset;
            if (ts == TimeSpan.Zero)
                return "00:00:00 (Giữ nguyên giờ gốc, không sinh .xmp)";

            string sign = ts < TimeSpan.Zero ? "-" : "+";
            var abs = ts.Duration();
            return $"{sign}{abs.Hours:D2}:{abs.Minutes:D2}:{abs.Seconds:D2} (Sinh Sidecar .xmp, giữ nguyên RAW)";
        }
    }

    public string PreviewDestinationPath
    {
        get
        {
            return CopyEngineService.BuildDestinationMediaDirectory(PrimaryRootFolder, BuildCurrentJobRequest());
        }
    }

    public string PreviewBackupPath
    {
        get
        {
            if (!EnableDualBackup || string.IsNullOrWhiteSpace(BackupRootFolder))
                return "Chưa bật Dual Backup";

            return CopyEngineService.BuildDestinationMediaDirectory(BackupRootFolder, BuildCurrentJobRequest());
        }
    }

    public MainViewModel(
        DriveWatcherService driveWatcherService,
        MetadataService metadataService,
        CopyEngineService copyEngineService)
    {
        _driveWatcherService = driveWatcherService;
        _metadataService = metadataService;
        _copyEngineService = copyEngineService;

        Directory.CreateDirectory(MetadataService.PersistentAppDataFolder);
        _settingsFilePath = Path.Combine(MetadataService.PersistentAppDataFolder, "user_settings.json");
        _localExeSettingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "user_settings.json");
        _historyFilePath = Path.Combine(MetadataService.PersistentAppDataFolder, "ingest_history.json");
        _localExeHistoryPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ingest_history.json");

        LoadUserSettings();
        LoadIngestHistory();

        FilterViewModel.RequestSyncFromIngest = () => SyncDestinationToFilterTab(overrideIfNotEmpty: true);

        // Đảm bảo Tên Khách luôn để trống mỗi lần mở app
        _clientName = string.Empty;

        _driveWatcherService.CardInserted += OnCardInserted;
        _driveWatcherService.CardRemoved += OnCardRemoved;

        _copyEngineService.ProgressChanged += OnCopyProgressChanged;
        _copyEngineService.JobCompleted += OnCopyJobCompleted;
        _copyEngineService.LogMessageEmitted += OnServiceLogEmitted;

        _isStartupEnabled = PlatformHelper.IsStartupEnabled();

        PropertyChanged += (_, e) =>
        {
            if (_isLoadingSettings) return;

            if (e.PropertyName == nameof(CameraSerialNumber))
            {
                string? matched = _metadataService.LookupPhotographerBySerial(CameraSerialNumber);
                if (!string.IsNullOrWhiteSpace(matched))
                {
                    PhotographerName = matched;
                }
            }

            if (e.PropertyName == nameof(PrimaryRootFolder) ||
                e.PropertyName == nameof(BackupRootFolder) ||
                e.PropertyName == nameof(EnableDualBackup) ||
                e.PropertyName == nameof(JobType) ||
                e.PropertyName == nameof(IsMultiCamera) ||
                e.PropertyName == nameof(PhotographerName) ||
                e.PropertyName == nameof(CameraMake) ||
                e.PropertyName == nameof(CameraModel) ||
                e.PropertyName == nameof(CameraSerialNumber) ||
                e.PropertyName == nameof(OffsetHours) ||
                e.PropertyName == nameof(OffsetMinutes) ||
                e.PropertyName == nameof(OffsetSeconds) ||
                e.PropertyName == nameof(AutoEjectAfterIngest) ||
                e.PropertyName == nameof(EnableThermalGuard) ||
                e.PropertyName == nameof(IncrementalSkipExisting) ||
                e.PropertyName == nameof(SplitByFileType) ||
                e.PropertyName == nameof(EnableCustomRename) ||
                e.PropertyName == nameof(RenameTemplate) ||
                e.PropertyName == nameof(PlaySoundOnComplete))
            {
                SaveUserSettings();
            }
        };

        AppendLog($"Khởi tạo Chép Thẻ Nhanh Pro (macOS Edition) hoàn tất. Đã tải {IngestHistory.Count} lịch sử đổ thẻ.");
    }

    private void LoadUserSettings()
    {
        try
        {
            string pathToLoad = File.Exists(_settingsFilePath) ? _settingsFilePath : _localExeSettingsPath;
            if (!File.Exists(pathToLoad)) return;

            _isLoadingSettings = true;
            string json = File.ReadAllText(pathToLoad, Encoding.UTF8);
            var settings = JsonSerializer.Deserialize<AppUserSettings>(json);
            if (settings != null)
            {
                if (!string.IsNullOrWhiteSpace(settings.PrimaryRootFolder))
                    PrimaryRootFolder = settings.PrimaryRootFolder;
                if (!string.IsNullOrWhiteSpace(settings.BackupRootFolder))
                    BackupRootFolder = settings.BackupRootFolder;

                EnableDualBackup = settings.EnableDualBackup;

                if (!string.IsNullOrWhiteSpace(settings.LastJobType))
                {
                    JobType = settings.LastJobType switch
                    {
                        "Wedding" => "Phóng sự cưới",
                        "Event" => "Sự kiện",
                        "Portrait" => "Chân dung - Nàng thơ",
                        "PreWedding" => "Pre-Wedding",
                        _ => settings.LastJobType
                    };
                }

                IsMultiCamera = settings.IsMultiCamera;
                if (!string.IsNullOrWhiteSpace(settings.LastPhotographerName))
                    PhotographerName = settings.LastPhotographerName;
                if (!string.IsNullOrWhiteSpace(settings.LastCameraMake))
                    CameraMake = settings.LastCameraMake;
                if (!string.IsNullOrWhiteSpace(settings.LastCameraModel))
                    CameraModel = settings.LastCameraModel;
                if (!string.IsNullOrWhiteSpace(settings.LastCameraSerialNumber))
                    CameraSerialNumber = settings.LastCameraSerialNumber;

                OffsetHours = settings.OffsetHours;
                OffsetMinutes = settings.OffsetMinutes;
                OffsetSeconds = settings.OffsetSeconds;

                AutoEjectAfterIngest = settings.AutoEjectAfterIngest;
                EnableThermalGuard = settings.EnableThermalGuard;
                IncrementalSkipExisting = settings.IncrementalSkipExisting;
                SplitByFileType = settings.SplitByFileType;
                EnableCustomRename = settings.EnableCustomRename;
                if (!string.IsNullOrWhiteSpace(settings.RenameTemplate))
                    RenameTemplate = settings.RenameTemplate;
                PlaySoundOnComplete = settings.PlaySoundOnComplete;
                IsDarkMode = settings.IsDarkMode;
                ThemeToggleText = IsDarkMode ? "☀️ Sáng" : "🌙 Tối";
            }
        }
        catch { }
        finally
        {
            _isLoadingSettings = false;
        }
    }

    private void SaveUserSettings()
    {
        try
        {
            var settings = new AppUserSettings
            {
                PrimaryRootFolder = PrimaryRootFolder,
                BackupRootFolder = BackupRootFolder,
                EnableDualBackup = EnableDualBackup,
                LastClientName = string.Empty,
                LastJobType = JobType,
                IsMultiCamera = IsMultiCamera,
                LastPhotographerName = PhotographerName,
                LastCameraMake = CameraMake,
                LastCameraModel = CameraModel,
                LastCameraSerialNumber = CameraSerialNumber,
                OffsetHours = OffsetHours,
                OffsetMinutes = OffsetMinutes,
                OffsetSeconds = OffsetSeconds,
                AutoEjectAfterIngest = AutoEjectAfterIngest,
                EnableThermalGuard = EnableThermalGuard,
                IncrementalSkipExisting = IncrementalSkipExisting,
                SplitByFileType = SplitByFileType,
                EnableCustomRename = EnableCustomRename,
                RenameTemplate = RenameTemplate,
                PlaySoundOnComplete = PlaySoundOnComplete,
                IsDarkMode = IsDarkMode
            };
            string json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_settingsFilePath, json, Encoding.UTF8);
            try { File.WriteAllText(_localExeSettingsPath, json, Encoding.UTF8); } catch { }
        }
        catch { }
    }

    public void LoadIngestHistory()
    {
        try
        {
            string pathToLoad = File.Exists(_historyFilePath) ? _historyFilePath : _localExeHistoryPath;
            if (!File.Exists(pathToLoad)) return;

            string json = File.ReadAllText(pathToLoad, Encoding.UTF8);
            var store = JsonSerializer.Deserialize<IngestHistoryStore>(json);
            if (store?.Entries != null)
            {
                IngestHistory.Clear();
                foreach (var entry in store.Entries.OrderByDescending(x => x.CompletedAt))
                {
                    IngestHistory.Add(entry);
                }
            }
        }
        catch { }
    }

    public void SaveIngestHistory()
    {
        try
        {
            var store = new IngestHistoryStore { Entries = IngestHistory.ToList() };
            string json = JsonSerializer.Serialize(store, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_historyFilePath, json, Encoding.UTF8);
            try { File.WriteAllText(_localExeHistoryPath, json, Encoding.UTF8); } catch { }
        }
        catch { }
    }

    private void RecordCompletedJobHistory(IngestJobSummary summary)
    {
        try
        {
            var entry = new IngestHistoryEntry
            {
                JobId = summary.Request.JobId,
                CompletedAt = summary.EndTime,
                ShootDateText = summary.Request.ShootDate.ToString("dd/MM/yyyy"),
                ClientName = summary.Request.ClientName,
                JobType = summary.Request.JobType,
                PhotographerName = summary.Request.PhotographerName,
                CameraModel = summary.Request.CameraModel,
                TotalFiles = summary.FileResults.Count,
                VerifiedFiles = summary.VerifiedCount,
                TotalSizeText = CopyEngineService.FormatBytes(summary.TotalBytesVerified),
                AverageSpeedMBps = Math.Round(summary.AverageSpeedMBps, 1),
                PrimaryDirectory = summary.PrimaryJobDirectory,
                BackupDirectory = summary.BackupJobDirectory,
                ReportFilePath = summary.ReportFilePath,
                SplitByFileType = summary.Request.SplitByFileType
            };

            IngestHistory.Insert(0, entry);
            while (IngestHistory.Count > 200)
            {
                IngestHistory.RemoveAt(IngestHistory.Count - 1);
            }
            SaveIngestHistory();
        }
        catch { }
    }

    public void ClearIngestHistory()
    {
        IngestHistory.Clear();
        SaveIngestHistory();
        AppendLog("[Lịch sử] Đã xóa toàn bộ danh sách lịch sử đổ thẻ.");
    }

    private async void OnCardInserted(object? sender, RemovableDriveEventArgs e)
    {
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            CardInsertedAttentionRequested?.Invoke(this, EventArgs.Empty);
            SourcePath = e.DriveRoot;
            AppendLog($"[Phát hiện thẻ] Đã cắm '{e.DriveRoot}' ({e.VolumeLabel}) - Tìm thấy {e.MediaFiles.Count} file Media.");
            await ApplyScannedFilesAndExtractExifAsync(e.MediaFiles);
        });
    }

    private void OnCardRemoved(object? sender, string driveRoot)
    {
        Dispatcher.UIThread.Post(() =>
        {
            AppendLog($"[Rút thẻ] Thiết bị '{driveRoot}' đã ngắt kết nối.");
            if (string.IsNullOrWhiteSpace(SourcePath) || SourcePath.StartsWith(driveRoot, StringComparison.OrdinalIgnoreCase))
            {
                ResetSourceMediaState($"Thiết bị {driveRoot} đã được rút ra. Sẵn sàng nhận thẻ nhớ hoặc box SSD mới.");
            }
            else
            {
                RefreshDriveSpaceTelemetry();
            }
        });
    }

    /// <summary>
    /// Đưa toàn bộ thông tin nguồn, số file, dung lượng và các nhóm ngày về trạng thái ban đầu khi rút thẻ.
    /// </summary>
    public void ResetSourceMediaState(string statusMessage)
    {
        SourcePath = string.Empty;
        _scannedMediaFiles = Array.Empty<string>();
        _scannedTotalBytes = 0;
        DetectedFileCount = 0;
        DetectedTotalSizeText = "0 MB";
        MediaBreakdownText = "RAW: 0 • JPG: 0 • VIDEO: 0";
        ShootDateGroups.Clear();
        HasMultipleShootDates = false;
        CardDatesStatusText = "Chưa cắm thẻ nhớ / box SSD";
        CurrentPhaseText = statusMessage;
        RefreshDriveSpaceTelemetry();
        OnPropertyChanged(nameof(SourceDriveCapacityText));
        OnPropertyChanged(nameof(SourceDriveUsedPercentage));
        OnPropertyChanged(nameof(PreviewRenamedFileText));
    }

    public async Task HandleStartupSourcePathAsync(string folderOrDrivePath)
    {
        if (string.IsNullOrWhiteSpace(folderOrDrivePath) || !Directory.Exists(folderOrDrivePath))
            return;

        SourcePath = folderOrDrivePath;
        AppendLog($"[Khởi động nhanh] Nhận đường dẫn nguồn: {folderOrDrivePath}");
        await ScanSourceFolderAsync();
    }

    [RelayCommand]
    public async Task ScanSourceFolderAsync()
    {
        if (string.IsNullOrWhiteSpace(SourcePath) || !Directory.Exists(SourcePath))
        {
            AppendLog($"[Cảnh báo] Đường dẫn nguồn không tồn tại: '{SourcePath}'");
            DetectedFileCount = 0;
            DetectedTotalSizeText = "0 MB";
            MediaBreakdownText = "RAW: 0 • JPG: 0 • VIDEO: 0";
            CardDatesStatusText = "Chưa quét ngày chụp trên thẻ";
            HasMultipleShootDates = false;
            _scannedTotalBytes = 0;
            RefreshDriveSpaceTelemetry();
            return;
        }

        _activeScanCts?.Cancel();
        _activeScanCts = new CancellationTokenSource();
        var ct = _activeScanCts.Token;

        try
        {
            CurrentPhaseText = $"Đang quét file Media và đọc EXIF tại {SourcePath}...";
            var files = await Task.Run(() => _driveWatcherService.ScanMediaFiles(SourcePath, ct), ct);
            await ApplyScannedFilesAndExtractExifAsync(files);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppendLog($"[Lỗi quét nguồn] {ex.Message}");
        }
    }

    private async Task ApplyScannedFilesAndExtractExifAsync(IReadOnlyList<string> files)
    {
        _scannedMediaFiles = files;
        DetectedFileCount = files.Count;

        var analysis = await Task.Run(() =>
        {
            long bytes = 0;
            int rawCount = 0;
            int jpgCount = 0;
            int videoCount = 0;
            var groupsDict = new Dictionary<DateTime, CardShootDateItem>();

            foreach (string f in files)
            {
                long fileLen = 0;
                DateTime fileDate = DateTime.Today;
                try
                {
                    var fi = new FileInfo(f);
                    fileLen = fi.Length;
                    fileDate = fi.LastWriteTime.Date;
                }
                catch { }

                bytes += fileLen;

                if (!groupsDict.TryGetValue(fileDate, out var group))
                {
                    group = new CardShootDateItem
                    {
                        Date = fileDate,
                        IsSelected = true
                    };
                    groupsDict[fileDate] = group;
                }

                group.FileCount++;
                group.TotalBytes += fileLen;
                group.FilePaths.Add(f);

                string ext = Path.GetExtension(f);
                if (DriveWatcherService.RawExtensions.Contains(ext))
                {
                    rawCount++;
                    group.RawCount++;
                }
                else if (VideoExtensions.Contains(ext))
                {
                    videoCount++;
                    group.VideoCount++;
                }
                else
                {
                    jpgCount++;
                    group.JpgCount++;
                }
            }

            var sortedGroups = groupsDict.Values.OrderByDescending(x => x.Date).ToList();
            return (bytes, rawCount, jpgCount, videoCount, sortedGroups);
        });

        _scannedTotalBytes = analysis.bytes;
        DetectedTotalSizeText = CopyEngineService.FormatBytes(analysis.bytes);
        MediaBreakdownText = $"RAW: {analysis.rawCount} • JPG: {analysis.jpgCount} • VIDEO: {analysis.videoCount}";

        ShootDateGroups.Clear();
        foreach (var grp in analysis.sortedGroups)
        {
            ShootDateGroups.Add(grp);
        }

        if (analysis.sortedGroups.Count > 1)
        {
            HasMultipleShootDates = true;
            string dateListPreview = string.Join(", ", analysis.sortedGroups.Take(3).Select(g => $"{g.Date:dd/MM} ({g.FileCount} file)"));
            if (analysis.sortedGroups.Count > 3)
            {
                dateListPreview += $", +{analysis.sortedGroups.Count - 3} ngày khác";
            }
            CardDatesStatusText = $"⚠ Chú ý: Thẻ có {analysis.sortedGroups.Count} ngày chụp ({dateListPreview}) - Nhấn [📅 Chọn Ngày] để lọc!";
            AppendLog($"[Cảnh báo thẻ nhiều ngày] Phát hiện ảnh từ {analysis.sortedGroups.Count} ngày chụp khác nhau trên thẻ ({dateListPreview}).");
        }
        else if (analysis.sortedGroups.Count == 1)
        {
            HasMultipleShootDates = false;
            var only = analysis.sortedGroups[0];
            CardDatesStatusText = $"✓ Thẻ đồng nhất 1 ngày chụp: {only.DateFormatted} ({only.FileCount} file - {only.TotalSizeText})";
        }
        else
        {
            HasMultipleShootDates = false;
            CardDatesStatusText = "Không có dữ liệu ngày chụp";
        }

        RefreshDriveSpaceTelemetry();
        OnPropertyChanged(nameof(PreviewRenamedFileText));

        if (files.Count == 0)
        {
            CurrentPhaseText = "Không tìm thấy file ảnh/video hợp lệ trong thư mục nguồn.";
            return;
        }

        var exif = await _metadataService.ExtractFirstFileExifAsync(files);
        CameraMake = exif.Make;
        CameraModel = exif.Model;

        if (!string.IsNullOrWhiteSpace(exif.SerialNumber))
        {
            CameraSerialNumber = exif.SerialNumber;
        }

        if (exif.DateTimeOriginal.HasValue)
        {
            ShootDate = exif.DateTimeOriginal.Value.Date;
        }

        if (!string.IsNullOrWhiteSpace(exif.MatchedPhotographerName))
        {
            PhotographerName = exif.MatchedPhotographerName;
            AppendLog($"[EXIF Match] Máy: {exif.Model} | Serial: {exif.SerialNumber} -> Thợ chụp: {PhotographerName}");
        }
        else
        {
            string serialInfo = string.IsNullOrWhiteSpace(exif.SerialNumber) ? "Không có trong EXIF" : exif.SerialNumber;
            AppendLog($"[EXIF] Máy: {exif.Model} | Serial: {serialInfo}. Bạn có thể bấm 'Lưu Thợ' để gắn tên thợ.");
        }

        CurrentPhaseText = $"Sẵn sàng Copy {files.Count} file ({DetectedTotalSizeText}) [RAW: {analysis.rawCount}, JPG: {analysis.jpgCount}, Video: {analysis.videoCount}].";
    }

    public void RefreshDriveSpaceTelemetry()
    {
        OnPropertyChanged(nameof(SourceDriveCapacityText));
        OnPropertyChanged(nameof(SourceDriveUsedPercentage));
        OnPropertyChanged(nameof(PrimaryDriveSpaceText));
        OnPropertyChanged(nameof(PrimaryDriveUsedPercentage));
        OnPropertyChanged(nameof(PrimaryDriveAfterCopyText));
        OnPropertyChanged(nameof(BackupDriveSpaceText));
    }

    /// <summary>
    /// Áp dụng bộ lọc chỉ sao chép các file thuộc những ngày chụp được người dùng tick chọn.
    /// </summary>
    public void ApplyDateFilter(IReadOnlyCollection<DateTime> selectedDates)
    {
        if (selectedDates == null || selectedDates.Count == 0) return;

        var selectedDateSet = new HashSet<DateTime>(selectedDates);
        var filteredFiles = new List<string>();
        long totalBytes = 0;
        int rawCount = 0, jpgCount = 0, vidCount = 0;

        foreach (var grp in ShootDateGroups)
        {
            grp.IsSelected = selectedDateSet.Contains(grp.Date);
            if (grp.IsSelected)
            {
                filteredFiles.AddRange(grp.FilePaths);
                totalBytes += grp.TotalBytes;
                rawCount += grp.RawCount;
                jpgCount += grp.JpgCount;
                vidCount += grp.VideoCount;
            }
        }

        _scannedMediaFiles = filteredFiles;
        _scannedTotalBytes = totalBytes;
        DetectedFileCount = filteredFiles.Count;
        DetectedTotalSizeText = CopyEngineService.FormatBytes(totalBytes);
        MediaBreakdownText = $"RAW: {rawCount} • JPG: {jpgCount} • VIDEO: {vidCount}";

        var latestSelected = ShootDateGroups.FirstOrDefault(x => x.IsSelected);
        if (latestSelected != null)
        {
            ShootDate = latestSelected.Date;
        }

        if (selectedDateSet.Count == ShootDateGroups.Count)
        {
            CardDatesStatusText = $"✓ Đã chọn toàn bộ {ShootDateGroups.Count} ngày chụp ({filteredFiles.Count} file • {DetectedTotalSizeText})";
        }
        else
        {
            CardDatesStatusText = $"✓ Đã lọc {selectedDateSet.Count}/{ShootDateGroups.Count} ngày ({filteredFiles.Count} file • {DetectedTotalSizeText}) - Bấm để đổi";
        }

        AppendLog($"[Lọc ngày chụp] Đã chọn {selectedDateSet.Count}/{ShootDateGroups.Count} ngày chụp ({filteredFiles.Count} file, {DetectedTotalSizeText}).");

        OnPropertyChanged(nameof(SourceDriveCapacityText));
        OnPropertyChanged(nameof(SourceDriveUsedPercentage));
        OnPropertyChanged(nameof(PreviewRenamedFileText));
        RefreshDriveSpaceTelemetry();
    }

    [RelayCommand]
    private void SaveCameraProfile()
    {
        if (string.IsNullOrWhiteSpace(CameraSerialNumber))
        {
            AppendLog("[Cấu hình Thợ] Vui lòng nhập Body Serial Number trước khi lưu.");
            return;
        }

        if (string.IsNullOrWhiteSpace(PhotographerName))
        {
            AppendLog("[Cấu hình Thợ] Vui lòng nhập Tên thợ phụ trách.");
            return;
        }

        _metadataService.SaveOrUpdateCameraProfile(CameraSerialNumber, PhotographerName);
        SaveUserSettings();
        AppendLog($"[Đã lưu vĩnh viễn] Serial '{CameraSerialNumber}' -> Thợ '{PhotographerName}'.");
    }

    [RelayCommand]
    private void OpenProfilesFile()
    {
        string path = _metadataService.ProfilesFilePath;
        if (File.Exists(path))
        {
            PlatformHelper.OpenTextFile(path);
        }
    }

    [RelayCommand]
    private async Task StartIngestJobAsync()
    {
        if (string.IsNullOrWhiteSpace(SourcePath) || !Directory.Exists(SourcePath))
        {
            AppendLog("[Lỗi] Vui lòng chọn ổ đĩa thẻ nhớ hoặc thư mục nguồn hợp lệ.");
            return;
        }

        if (string.IsNullOrWhiteSpace(PrimaryRootFolder))
        {
            AppendLog("[Lỗi] Vui lòng chọn thư mục Ổ cứng chính.");
            return;
        }

        if (_scannedMediaFiles.Count == 0)
        {
            await ScanSourceFolderAsync();
            if (_scannedMediaFiles.Count == 0)
            {
                AppendLog("[Cảnh báo] Không có file ảnh/video nào để sao chép.");
                return;
            }
        }

        SaveUserSettings();
        var request = BuildCurrentJobRequest();

        try
        {
            IsJobRunning = true;
            IsJobPaused = false;
            PendingJobsInQueue = _copyEngineService.PendingJobCount + 1;

            var summary = await _copyEngineService.EnqueueJobAsync(request);
            LastReportFilePath = summary.ReportFilePath;
            RecordCompletedJobHistory(summary);
            RefreshDriveSpaceTelemetry();

            string skipNote = summary.SkippedExistingCount > 0 ? $", Bỏ qua {summary.SkippedExistingCount} file trùng" : string.Empty;
            string ejectNote = summary.WasEjected ? " | Đã Eject thẻ an toàn!" : string.Empty;
            CurrentPhaseText = $"Hoàn tất: Đã xác thực {summary.VerifiedCount}/{summary.FileResults.Count} file ({summary.AverageSpeedMBps:F1} MB/s{skipNote}){ejectNote}";

            if (PlaySoundOnComplete)
            {
                PlatformHelper.PlayCompletionSound();
            }

            JobFinishedNotification?.Invoke(this, summary);
        }
        catch (OperationCanceledException)
        {
            CurrentPhaseText = "Đã hủy tiến trình Copy theo yêu cầu.";
            AppendLog("[Đã hủy] Tiến trình Copy đã dừng lại.");
        }
        catch (Exception ex)
        {
            CurrentPhaseText = $"Lỗi Copy: {ex.Message}";
            AppendLog($"[Lỗi nghiêm trọng] {ex.Message}");
        }
        finally
        {
            PendingJobsInQueue = _copyEngineService.PendingJobCount;
            IsJobRunning = _copyEngineService.PendingJobCount > 0;
            IsJobPaused = _copyEngineService.IsPaused;
            RefreshDriveSpaceTelemetry();
        }
    }

    [RelayCommand]
    private void TogglePauseJob()
    {
        if (!IsJobRunning) return;
        bool nextPauseState = !IsJobPaused;
        _copyEngineService.SetPaused(nextPauseState);
        IsJobPaused = nextPauseState;
        CurrentPhaseText = nextPauseState
            ? "⏸ Đang tạm dừng - Nhấn 'Tiếp Tục' để chép tiếp..."
            : "Đang tiếp tục sao chép...";
    }

    [RelayCommand]
    private void CancelCurrentJob()
    {
        if (!IsJobRunning) return;
        _copyEngineService.CancelActiveJob();
        IsJobPaused = false;
    }

    [RelayCommand]
    public void OpenLastReport()
    {
        if (!string.IsNullOrWhiteSpace(LastReportFilePath) && File.Exists(LastReportFilePath))
        {
            PlatformHelper.OpenTextFile(LastReportFilePath);
            return;
        }

        string candidate = Path.Combine(
            CopyEngineService.BuildJobRootDirectory(PrimaryRootFolder, BuildCurrentJobRequest()),
            "ingest_report.txt");

        if (File.Exists(candidate))
        {
            PlatformHelper.OpenTextFile(candidate);
        }
        else
        {
            AppendLog("[Báo cáo] Chưa có file ingest_report.txt cho Job hiện tại.");
        }
    }

    [RelayCommand]
    private void ToggleStartup()
    {
        var (success, message) = PlatformHelper.SetStartupEnabled(IsStartupEnabled);
        AppendLog($"[macOS Startup] {message}");
        if (!success)
        {
            IsStartupEnabled = PlatformHelper.IsStartupEnabled();
        }
    }

    [RelayCommand]
    public void OpenDestinationFolder()
    {
        string target = CopyEngineService.BuildDestinationMediaDirectory(PrimaryRootFolder, BuildCurrentJobRequest());
        if (Directory.Exists(target))
        {
            PlatformHelper.OpenFolder(target);
        }
        else if (Directory.Exists(PrimaryRootFolder))
        {
            PlatformHelper.OpenFolder(PrimaryRootFolder);
        }
    }

    private IngestJobRequest BuildCurrentJobRequest()
    {
        return new IngestJobRequest
        {
            SourceDriveOrFolder = SourcePath,
            PrimaryRootFolder = PrimaryRootFolder,
            BackupRootFolder = BackupRootFolder,
            EnableDualBackup = EnableDualBackup,
            ShootDate = ShootDate,
            ClientName = ClientName,
            JobType = JobType,
            IsMultiCamera = IsMultiCamera,
            PhotographerName = PhotographerName,
            CameraMake = CameraMake,
            CameraModel = CameraModel,
            CameraSerialNumber = CameraSerialNumber,
            TimeOffset = CurrentTimeOffset,
            IncrementalSkipExisting = IncrementalSkipExisting,
            SplitByFileType = SplitByFileType,
            EnableCustomRename = EnableCustomRename,
            RenameTemplate = RenameTemplate,
            AutoEjectAfterIngest = AutoEjectAfterIngest,
            EnableThermalGuard = EnableThermalGuard,
            MediaFiles = _scannedMediaFiles
        };
    }

    private void OnCopyProgressChanged(object? sender, IngestProgressInfo e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsJobRunning) return;
            ProgressPercentage = e.Percentage;
            CurrentPhaseText = $"{e.Phase} ({e.CompletedFiles}/{e.TotalFiles})";
            TransferSpeedText = $"{e.SpeedMegabytesPerSecond:F2} MB/s";
            PendingJobsInQueue = e.PendingJobsInQueue;
            IsJobPaused = e.IsPaused;
            EtaText = e.EstimatedRemaining.HasValue
                ? $"{(int)e.EstimatedRemaining.Value.TotalMinutes:D2}:{e.EstimatedRemaining.Value.Seconds:D2}"
                : "--:--";
        });
    }

    private void OnCopyJobCompleted(object? sender, IngestJobSummary e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            PendingJobsInQueue = _copyEngineService.PendingJobCount;
            LastReportFilePath = e.ReportFilePath;
            RefreshDriveSpaceTelemetry();
            if (!string.IsNullOrWhiteSpace(e.PrimaryJobDirectory))
            {
                FilterViewModel.SourceDirectory = e.PrimaryJobDirectory;
                FilterViewModel.DestinationDirectory = e.PrimaryJobDirectory;
            }
        });
    }

    private void OnServiceLogEmitted(object? sender, string message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            AppendLog(message);
        });
    }

    public void AppendLog(string text)
    {
        ActivityLogs.Insert(0, text);
        if (ActivityLogs.Count > 300)
        {
            ActivityLogs.RemoveAt(ActivityLogs.Count - 1);
        }
    }

    public void Dispose()
    {
        SaveUserSettings();
        _activeScanCts?.Cancel();
        _activeScanCts?.Dispose();
        _driveWatcherService.Dispose();
        _copyEngineService.Dispose();
    }
}
