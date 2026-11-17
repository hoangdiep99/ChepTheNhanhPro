using System;
using System.Collections.Generic;

namespace PhotoIngestTool.Models;

/// <summary>
/// Thông tin EXIF trích xuất từ file ảnh đầu tiên trên thẻ nhớ.
/// </summary>
public class CameraExifInfo
{
    public string Make { get; set; } = "Unknown";
    public string Model { get; set; } = "UnknownCam";
    public string SerialNumber { get; set; } = string.Empty;
    public DateTime? DateTimeOriginal { get; set; }
    public string? MatchedPhotographerName { get; set; }
    public string SampleFilePath { get; set; } = string.Empty;
}

/// <summary>
/// Cấu hình ánh xạ BodySerialNumber -> Tên thợ chụp trong camera_profiles.json.
/// </summary>
public class CameraProfile
{
    public string SerialNumber { get; set; } = string.Empty;
    public string PhotographerName { get; set; } = string.Empty;
    public string CameraModel { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
}

/// <summary>
/// Cấu hình gốc của file camera_profiles.json.
/// </summary>
public class CameraProfileStore
{
    public Dictionary<string, string> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["7609738"] = "Anh Tuấn",
        ["482910"] = "Anh Tuấn",
        ["309182"] = "Minh Hoàng",
        ["882104"] = "Hải Đăng"
    };
}

/// <summary>
/// Cấu hình người dùng lưu tự động vào user_settings.json giữa các phiên làm việc.
/// </summary>
public class AppUserSettings
{
    public string PrimaryRootFolder { get; set; } = string.Empty;
    public string BackupRootFolder { get; set; } = string.Empty;
    public bool EnableDualBackup { get; set; }
    public string LastClientName { get; set; } = "KhachHang";
    public string LastJobType { get; set; } = "Wedding";
    public bool IsMultiCamera { get; set; }
    public string LastPhotographerName { get; set; } = "Anh Tuấn";
    public string LastCameraMake { get; set; } = "NIKON CORPORATION";
    public string LastCameraModel { get; set; } = "NIKON Z5_2";
    public string LastCameraSerialNumber { get; set; } = "7609738";
    public int OffsetHours { get; set; }
    public int OffsetMinutes { get; set; }
    public int OffsetSeconds { get; set; }
    public bool AutoEjectAfterIngest { get; set; } = true;
    public bool EnableThermalGuard { get; set; } = true;
    public bool IncrementalSkipExisting { get; set; } = true;
    public bool SplitByFileType { get; set; } = true;
    public bool EnableCustomRename { get; set; }
    public string RenameTemplate { get; set; } = "{Photographer}_{Original}";
    public bool PlaySoundOnComplete { get; set; } = true;
    public bool IsDarkMode { get; set; } = true;
}

/// <summary>
/// Yêu cầu đổ thẻ (1 Job trong hàng đợi xử lý tuần tự).
/// </summary>
public class IngestJobRequest
{
    public Guid JobId { get; init; } = Guid.NewGuid();
    public string SourceDriveOrFolder { get; set; } = string.Empty;
    public string PrimaryRootFolder { get; set; } = string.Empty;
    public string? BackupRootFolder { get; set; }
    public bool EnableDualBackup { get; set; }

    public DateTime ShootDate { get; set; } = DateTime.Today;
    public string ClientName { get; set; } = string.Empty;
    public string JobType { get; set; } = "Phóng sự cưới";

    public bool IsMultiCamera { get; set; }
    public string PhotographerName { get; set; } = string.Empty;
    public string CameraMake { get; set; } = string.Empty;
    public string CameraModel { get; set; } = string.Empty;
    public string CameraSerialNumber { get; set; } = string.Empty;

    /// <summary>
    /// Độ lệch thời gian (+/- giờ, phút, giây) của máy phụ so với máy chính.
    /// </summary>
    public TimeSpan TimeOffset { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// Bỏ qua các file đã được copy trước đó (khớp tên + kích thước byte) khi đổ bồi thêm ảnh từ cùng 1 thẻ.
    /// </summary>
    public bool IncrementalSkipExisting { get; set; } = true;

    /// <summary>
    /// Tự động phân loại file vào thư mục con RAW / JPG / Video (Mặc định bật).
    /// </summary>
    public bool SplitByFileType { get; set; } = true;

    /// <summary>
    /// Đổi tên file khi copy sang ổ đích (không chạm vào file trên thẻ nhớ).
    /// </summary>
    public bool EnableCustomRename { get; set; }
    public string RenameTemplate { get; set; } = "{Photographer}_{Original}";

    public bool AutoEjectAfterIngest { get; set; } = true;
    public bool EnableThermalGuard { get; set; } = true;
    public IReadOnlyList<string> MediaFiles { get; set; } = Array.Empty<string>();
}

/// <summary>
/// Kết quả sao chép và kiểm tra xxHash64 của từng file.
/// </summary>
public class FileTransferResult
{
    public string SourceFilePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string DestinationFileName { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public ulong SourceHash { get; set; }
    public ulong PrimaryDestHash { get; set; }
    public ulong? BackupDestHash { get; set; }
    public string PrimaryDestPath { get; set; } = string.Empty;
    public string? BackupDestPath { get; set; }
    public bool SidecarGenerated { get; set; }
    public bool IsVerified { get; set; }
    public bool WasSkippedExisting { get; set; }
    public int RetryAttempts { get; set; }
    public string? ErrorMessage { get; set; }

    public string SourceHashHex => WasSkippedExisting ? "SKIPPED-EXISTING" : $"{SourceHash:X16}";
    public string PrimaryHashHex => WasSkippedExisting ? "SKIPPED-EXISTING" : $"{PrimaryDestHash:X16}";
    public string BackupHashHex => WasSkippedExisting
        ? "SKIPPED-EXISTING"
        : (BackupDestHash.HasValue ? $"{BackupDestHash.Value:X16}" : "N/A");
}

/// <summary>
/// Thông tin tiến trình gửi lên UI theo thời gian thực (kèm ETA).
/// </summary>
public class IngestProgressInfo
{
    public Guid JobId { get; set; }
    public string CurrentFileName { get; set; } = string.Empty;
    public int CompletedFiles { get; set; }
    public int TotalFiles { get; set; }
    public long TransferredBytes { get; set; }
    public long TotalBytes { get; set; }
    public double SpeedMegabytesPerSecond { get; set; }
    public TimeSpan? EstimatedRemaining { get; set; }
    public string Phase { get; set; } = string.Empty;
    public int PendingJobsInQueue { get; set; }
    public bool IsPaused { get; set; }

    public double Percentage => TotalBytes > 0
        ? Math.Clamp((double)TransferredBytes / TotalBytes * 100.0, 0, 100)
        : 0;
}

/// <summary>
/// Báo cáo tổng kết sau khi hoàn tất 1 Job đổ thẻ.
/// </summary>
public class IngestJobSummary
{
    public IngestJobRequest Request { get; set; } = new();
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public string PrimaryJobDirectory { get; set; } = string.Empty;
    public string? BackupJobDirectory { get; set; }
    public List<FileTransferResult> FileResults { get; set; } = new();
    public long TotalBytesVerified { get; set; }
    public int VerifiedCount { get; set; }
    public int SkippedExistingCount { get; set; }
    public int SkippedOrFailedCount { get; set; }
    public bool WasEjected { get; set; }
    public string ReportFilePath { get; set; } = string.Empty;

    public TimeSpan Duration => EndTime - StartTime;
    public double AverageSpeedMBps => Duration.TotalSeconds > 0
        ? (TotalBytesVerified / (1024.0 * 1024.0)) / Duration.TotalSeconds
        : 0;
}

/// <summary>
/// Bản ghi lịch sử 1 lần đổ thẻ thành công (lưu trong ingest_history.json).
/// </summary>
public class IngestHistoryEntry
{
    public Guid JobId { get; set; }
    public DateTime CompletedAt { get; set; } = DateTime.Now;
    public string ShootDateText { get; set; } = string.Empty;
    public string ClientName { get; set; } = string.Empty;
    public string JobType { get; set; } = string.Empty;
    public string PhotographerName { get; set; } = string.Empty;
    public string CameraModel { get; set; } = string.Empty;
    public int TotalFiles { get; set; }
    public int VerifiedFiles { get; set; }
    public string TotalSizeText { get; set; } = string.Empty;
    public double AverageSpeedMBps { get; set; }
    public string PrimaryDirectory { get; set; } = string.Empty;
    public string? BackupDirectory { get; set; }
    public string ReportFilePath { get; set; } = string.Empty;
    public bool SplitByFileType { get; set; }
}

public class IngestHistoryStore
{
    public List<IngestHistoryEntry> Entries { get; set; } = new();
}

/// <summary>
/// Thống kê chi tiết các file theo từng ngày chụp phát hiện trên thẻ nhớ.
/// </summary>
public class CardShootDateItem
{
    public bool IsSelected { get; set; } = true;
    public DateTime Date { get; set; }
    public string DateFormatted => Date.ToString("dd/MM/yyyy");
    public int FileCount { get; set; }
    public long TotalBytes { get; set; }
    public int RawCount { get; set; }
    public int JpgCount { get; set; }
    public int VideoCount { get; set; }
    public List<string> FilePaths { get; set; } = new();

    public string TotalSizeText
    {
        get
        {
            if (TotalBytes <= 0) return "0 MB";
            double mb = TotalBytes / (1024.0 * 1024.0);
            if (mb < 1024.0) return $"{mb:F1} MB";
            return $"{(mb / 1024.0):F2} GB";
        }
    }
}

/// <summary>
/// Thông tin bản cập nhật mới kiểm tra từ máy chủ GitHub.
/// </summary>
public class AppUpdateInfo
{
    public string LatestVersion { get; set; } = "1.0.0";
    public string ReleaseDate { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Changelog { get; set; } = string.Empty;
    public string DownloadUrlWindows { get; set; } = string.Empty;
    public string DownloadUrlMac { get; set; } = string.Empty;
    public bool IsUpdateAvailable { get; set; }
}

