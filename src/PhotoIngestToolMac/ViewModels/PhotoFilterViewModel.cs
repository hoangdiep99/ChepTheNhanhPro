using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoIngestTool.Models;
using PhotoIngestTool.Services;

namespace PhotoIngestTool.ViewModels;

public partial class PhotoFilterViewModel : ObservableObject
{
    private readonly PhotoFilterService _filterService = new();
    private CancellationTokenSource? _activeCts;
    private readonly StringBuilder _logBuilder = new();
    private readonly string _filterHistoryFilePath;
    private readonly string _localExeFilterHistoryPath;
    public ObservableCollection<FilterHistoryEntry> FilterHistory { get; } = new();

    public event Func<string, string, Task>? AlertRequested;
    public event Func<string, string, Task<bool>>? ConfirmRequested;
    public event Func<string, Task>? ClipboardCopyRequested;

    [ObservableProperty]
    private string _sourceDirectory = string.Empty;

    [ObservableProperty]
    private string _destinationDirectory = string.Empty;

    [ObservableProperty]
    private string _fileTypes = string.Empty;

    [ObservableProperty]
    private string _sequenceText = string.Empty;

    [ObservableProperty]
    private bool _createSubfolder = true;

    [ObservableProperty]
    private string _subfolderName = $"Auto loc anh {DateTime.Now:dd-MM-yyyy}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotFiltering))]
    private bool _isFiltering;

    public bool IsNotFiltering => !IsFiltering;

    [ObservableProperty]
    private double _progressPercentage;

    [ObservableProperty]
    private string _statusText = "Sẵn sàng lọc ảnh theo danh sách mã/số thứ tự.";

    [ObservableProperty]
    private string _filterLogText = string.Empty;

    [ObservableProperty]
    private string _lastDestinationFolder = string.Empty;

    // === ĐỐI SOÁT & BÁO CÁO MÃ THIẾU (MISSING INSPECTOR) ===
    [ObservableProperty]
    private bool _hasMissingCodes;

    [ObservableProperty]
    private int _missingCodesCount;

    [ObservableProperty]
    private string _missingCodesCountText = string.Empty;

    [ObservableProperty]
    private string _missingCodesSummaryText = string.Empty;

    [ObservableProperty]
    private string _missingCodesRawList = string.Empty;

    [ObservableProperty]
    private string _missingCodesCopyText = string.Empty;

    public ObservableCollection<string> FileTypePresets { get; } = new()
    {
        "",
        "jpg, png, jpeg",
        "jpg",
        "png",
        "jpeg",
        "arw",
        "cr2, cr3",
        "nef",
        "raf",
        "dng, raw"
    };

    public Action? RequestSyncFromIngest { get; set; }

    public PhotoFilterViewModel()
    {
        _filterHistoryFilePath = Path.Combine(MetadataService.PersistentAppDataFolder, "filter_history.json");
        _localExeFilterHistoryPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "filter_history.json");
        LoadFilterHistory();
    }

    public void LoadFilterHistory()
    {
        try
        {
            string pathToLoad = File.Exists(_filterHistoryFilePath) ? _filterHistoryFilePath : _localExeFilterHistoryPath;
            if (File.Exists(pathToLoad))
            {
                string json = File.ReadAllText(pathToLoad, Encoding.UTF8);
                var store = JsonSerializer.Deserialize<FilterHistoryStore>(json);
                if (store?.Entries != null)
                {
                    FilterHistory.Clear();
                    foreach (var entry in store.Entries)
                    {
                        FilterHistory.Add(entry);
                    }
                }
            }
        }
        catch { }
    }

    public void SaveFilterHistory()
    {
        try
        {
            var store = new FilterHistoryStore { Entries = FilterHistory.ToList() };
            string json = JsonSerializer.Serialize(store, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_filterHistoryFilePath, json, Encoding.UTF8);
            try { File.WriteAllText(_localExeFilterHistoryPath, json, Encoding.UTF8); } catch { }
        }
        catch { }
    }

    public void RecordCompletedFilterHistory(PhotoFilterResult result, PhotoFilterRequest request)
    {
        try
        {
            var entry = new FilterHistoryEntry
            {
                FilterId = Guid.NewGuid(),
                FilteredAt = DateTime.Now,
                SourceDirectory = request.SourceDirectory,
                DestinationDirectory = request.DestinationDirectory,
                EffectiveDestinationFolder = result.EffectiveDestinationFolder,
                FileTypes = string.IsNullOrWhiteSpace(request.FileTypes) ? "Tất cả file ảnh" : request.FileTypes,
                RawSequenceText = request.SequenceText,
                RequestedCodesCount = result.RequestedCodes.Count,
                FoundCodesCount = result.FoundCodes.Count,
                MissingCodesCount = result.MissingCodes.Count,
                MissingCodes = result.MissingCodes.ToList(),
                FilteredFilesCount = result.SuccessCount,
                ElapsedSeconds = result.ElapsedSeconds
            };

            FilterHistory.Insert(0, entry);
            while (FilterHistory.Count > 200)
            {
                FilterHistory.RemoveAt(FilterHistory.Count - 1);
            }
            SaveFilterHistory();
        }
        catch { }
    }

    public void ClearFilterHistory()
    {
        FilterHistory.Clear();
        SaveFilterHistory();
    }

    [RelayCommand]
    public void SelectFileTypePreset(string preset)
    {
        FileTypes = preset ?? string.Empty;
    }

    [RelayCommand]
    public void SyncFromIngest()
    {
        RequestSyncFromIngest?.Invoke();
    }

    [RelayCommand]
    public void OpenFacebook()
    {
        PlatformHelper.OpenUrl("https://www.facebook.com/diepfoto/");
    }

    [RelayCommand]
    public void OpenDestinationFolder()
    {
        string target = !string.IsNullOrWhiteSpace(LastDestinationFolder) && Directory.Exists(LastDestinationFolder)
            ? LastDestinationFolder
            : (!string.IsNullOrWhiteSpace(DestinationDirectory) && Directory.Exists(DestinationDirectory)
                ? DestinationDirectory
                : SourceDirectory);

        if (!string.IsNullOrWhiteSpace(target) && Directory.Exists(target))
        {
            PlatformHelper.OpenFolder(target);
        }
    }

    [RelayCommand]
    public async Task CopyMissingCodesAsync()
    {
        if (string.IsNullOrWhiteSpace(MissingCodesCopyText))
            return;

        try
        {
            if (ClipboardCopyRequested != null)
            {
                await ClipboardCopyRequested.Invoke(MissingCodesCopyText);
            }
            StatusText = $"📋 Đã copy {MissingCodesCount} mã thiếu vào Clipboard! (Sẵn sàng gửi Zalo/Facebook)";
            if (AlertRequested != null)
            {
                await AlertRequested.Invoke(
                    "Đã Copy Mã Thiếu",
                    $"Đã sao chép danh sách {MissingCodesCount} mã thiếu vào bộ nhớ tạm (Clipboard)!\n\nNội dung đã copy:\n{MissingCodesCopyText}\n\nBạn có thể dán ngay (Cmd/Ctrl + V) gửi cho khách hàng qua Zalo hoặc Facebook Messenger.");
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Lỗi khi copy Clipboard: {ex.Message}";
        }
    }

    [RelayCommand]
    public void ClearLog()
    {
        _logBuilder.Clear();
        FilterLogText = string.Empty;
        StatusText = "Đã xóa nhật ký.";
        HasMissingCodes = false;
        MissingCodesCount = 0;
        MissingCodesCountText = string.Empty;
        MissingCodesSummaryText = string.Empty;
        MissingCodesRawList = string.Empty;
        MissingCodesCopyText = string.Empty;
    }

    [RelayCommand]
    public void CancelFilter()
    {
        if (IsFiltering && _activeCts != null)
        {
            StatusText = "Đang dừng tiến trình lọc ảnh...";
            _activeCts.Cancel();
        }
    }

    [RelayCommand]
    public async Task StartFilterAsync()
    {
        if (IsFiltering) return;

        if (string.IsNullOrWhiteSpace(SourceDirectory) || !Directory.Exists(SourceDirectory))
        {
            if (AlertRequested != null)
            {
                await AlertRequested.Invoke("Thiếu Thư Mục Nguồn", "Vui lòng chọn thư mục ảnh nguồn hợp lệ trước khi lọc!");
            }
            return;
        }

        if (string.IsNullOrWhiteSpace(SequenceText))
        {
            if (ConfirmRequested != null)
            {
                bool proceed = await ConfirmRequested.Invoke(
                    "Chưa Nhập Mã Ảnh",
                    "Bạn chưa nhập danh sách mã ảnh cần tìm.\nNếu để trống, phần mềm sẽ copy TOÀN BỘ ảnh phù hợp định dạng sang thư mục đích.\n\nBạn có muốn tiếp tục không?");

                if (!proceed) return;
            }
        }

        IsFiltering = true;
        ProgressPercentage = 0;
        StatusText = "Đang chuẩn bị quét thư mục...";
        _logBuilder.Clear();
        FilterLogText = "🚀 Bắt đầu tiến trình lọc ảnh...\n";

        // Reset trạng thái đối soát mã thiếu
        HasMissingCodes = false;
        MissingCodesCount = 0;
        MissingCodesCountText = string.Empty;
        MissingCodesSummaryText = string.Empty;
        MissingCodesRawList = string.Empty;
        MissingCodesCopyText = string.Empty;

        _activeCts = new CancellationTokenSource();

        var request = new PhotoFilterRequest
        {
            SourceDirectory = SourceDirectory,
            DestinationDirectory = DestinationDirectory,
            SequenceText = SequenceText,
            FileTypes = FileTypes,
            CreateSubfolder = CreateSubfolder,
            SubfolderName = SubfolderName
        };

        var progress = new Progress<PhotoFilterProgress>(p =>
        {
            ProgressPercentage = p.ProgressPercentage;
            StatusText = p.StatusMessage;
            if (!string.IsNullOrWhiteSpace(p.LogMessage))
            {
                _logBuilder.AppendLine(p.LogMessage);
                FilterLogText = _logBuilder.ToString();
            }
        });

        try
        {
            var result = await _filterService.FilterImagesAsync(request, progress, _activeCts.Token);
            LastDestinationFolder = result.EffectiveDestinationFolder;

            if (result.WasCancelled)
            {
                StatusText = "❌ Đã hủy lọc ảnh theo yêu cầu.";
                _logBuilder.AppendLine("\n❌ Tiến trình đã bị hủy.");
            }
            else
            {
                ProgressPercentage = 100;
                StatusText = $"✅ Hoàn tất! Tìm thấy {result.SuccessCount} ảnh ({result.ElapsedSeconds:F2}s).";

                // Xử lý báo cáo đối soát mã thiếu (Missing Inspector)
                if (result.MissingCodes.Count > 0)
                {
                    HasMissingCodes = true;
                    MissingCodesCount = result.MissingCodes.Count;
                    MissingCodesCountText = $"{result.MissingCodes.Count} MÃ";
                    MissingCodesRawList = string.Join(", ", result.MissingCodes);
                    MissingCodesCopyText = $"Mã ảnh không tìm thấy ({result.MissingCodes.Count} mã): {MissingCodesRawList}";
                    MissingCodesSummaryText = $"Các mã không tìm thấy trong thư mục ({result.MissingCodes.Count} mã): {MissingCodesRawList}";
                }

                var summary = new StringBuilder();
                summary.AppendLine("\n" + new string('=', 45));
                summary.AppendLine($"✔️ KẾT QUẢ: Đã lọc thành công {result.SuccessCount} ảnh trong {result.ElapsedSeconds:F2}s");
                summary.AppendLine($"📁 Thư mục lưu: {result.EffectiveDestinationFolder}");

                if (result.RequestedCodes.Count > 0)
                {
                    summary.AppendLine(new string('-', 45));
                    summary.AppendLine("📋 BÁO CÁO ĐỐI SOÁT MÃ ẢNH (MISSING INSPECTOR):");
                    summary.AppendLine($"- Tổng số mã khách yêu cầu: {result.RequestedCodes.Count} mã");
                    summary.AppendLine($"- Đã tìm thấy & lọc: {result.FoundCodes.Count} mã ({result.FilteredFiles.Count} file)");
                    if (result.MissingCodes.Count > 0)
                    {
                        summary.AppendLine($"- ⚠️ MÃ THIẾU / KHÔNG TÌM THẤY ({result.MissingCodes.Count} mã):");
                        summary.AppendLine($"  {string.Join(", ", result.MissingCodes)}");
                        summary.AppendLine("👉 Bấm nút [📋 Copy Mã Thiếu] bên trên để gửi đối soát cho khách hàng!");
                    }
                    else
                    {
                        summary.AppendLine("- ✅ Tìm thấy đầy đủ 100% mã ảnh yêu cầu trong thư mục nguồn!");
                    }
                    summary.AppendLine(new string('-', 45));
                }

                if (result.SkippedCount > 0)
                {
                    summary.AppendLine($"⏭️ Bỏ qua {result.SkippedCount} file (đã có sẵn)");
                }
                if (result.ErrorCount > 0)
                {
                    summary.AppendLine($"⚠️ Gặp {result.ErrorCount} lỗi sao chép");
                }
                summary.AppendLine(new string('=', 45));

                _logBuilder.AppendLine(summary.ToString());
                FilterLogText = _logBuilder.ToString();

                // Ghi nhận vào lịch sử lọc ảnh
                RecordCompletedFilterHistory(result, request);

                // Tự động mở thư mục đích
                if (!string.IsNullOrWhiteSpace(result.EffectiveDestinationFolder) &&
                    Directory.Exists(result.EffectiveDestinationFolder))
                {
                    PlatformHelper.OpenFolder(result.EffectiveDestinationFolder);
                }

                if (AlertRequested != null)
                {
                    string missingNotice = result.MissingCodes.Count > 0
                        ? $"\n\n⚠️ ĐỐI SOÁT: Phát hiện {result.MissingCodes.Count} mã không tìm thấy ảnh trong thư mục nguồn!\n👉 Bấm nút [📋 Copy Mã Thiếu] bên trên để gửi đối soát cho khách."
                        : (result.RequestedCodes.Count > 0 ? "\n\n✅ ĐỐI SOÁT: Đã tìm thấy đầy đủ 100% mã ảnh khách yêu cầu!" : "");

                    await AlertRequested.Invoke(
                        "Lọc Ảnh Hoàn Tất",
                        $"Đã lọc thành công {result.SuccessCount} ảnh phù hợp!\n\nThời gian xử lý: {result.ElapsedSeconds:F2} giây\nThư mục đích: {result.EffectiveDestinationFolder}{missingNotice}");
                }
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Lỗi: {ex.Message}";
            _logBuilder.AppendLine($"\n❌ LỖI HỆ THỐNG: {ex.Message}");
            FilterLogText = _logBuilder.ToString();

            if (AlertRequested != null)
            {
                await AlertRequested.Invoke("Lỗi Lọc Ảnh", $"Đã xảy ra lỗi khi lọc ảnh:\n{ex.Message}");
            }
        }
        finally
        {
            IsFiltering = false;
            _activeCts?.Dispose();
            _activeCts = null;
        }
    }
}
