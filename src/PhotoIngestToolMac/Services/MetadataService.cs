using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using PhotoIngestTool.Models;

namespace PhotoIngestTool.Services;

/// <summary>
/// Module 2: Đọc nhanh EXIF (Make, Model, BodySerialNumber, DateTimeOriginal),
/// tra cứu thợ chụp từ camera_profiles.json (lưu bền vững tại %LocalAppData%\PhotoIngestTool và đồng bộ cùng thư mục App),
/// và sinh file Sidecar .xmp bù lệch giờ (Time Sync) mà TUYỆT ĐỐI KHÔNG can thiệp vào file RAW gốc.
/// </summary>
public sealed class MetadataService
{
    public static readonly string PersistentAppDataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PhotoIngestTool");

    private readonly string _primaryProfilesPath;
    private readonly string _localExeProfilesPath;
    private CameraProfileStore _profileStore = new();
    private readonly object _syncLock = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public MetadataService(string? customProfilePath = null)
    {
        System.IO.Directory.CreateDirectory(PersistentAppDataFolder);
        _primaryProfilesPath = customProfilePath ?? Path.Combine(PersistentAppDataFolder, "camera_profiles.json");
        _localExeProfilesPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "camera_profiles.json");
        LoadCameraProfiles();
    }

    public string ProfilesFilePath => _primaryProfilesPath;

    /// <summary>
    /// Đọc và hợp nhất (Merge) danh sách cấu hình SerialNumber -> TênThợ từ cả
    /// %LocalAppData%\PhotoIngestTool\camera_profiles.json và thư mục chạy ứng dụng để không bao giờ mất dữ liệu khi build lại.
    /// </summary>
    public void LoadCameraProfiles()
    {
        lock (_syncLock)
        {
            var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["7609738"] = "Anh Tuấn",
                ["482910"] = "Anh Tuấn",
                ["309182"] = "Minh Hoàng",
                ["882104"] = "Hải Đăng"
            };

            MergeProfilesFromFile(_localExeProfilesPath, merged);
            MergeProfilesFromFile(_primaryProfilesPath, merged);

            _profileStore = new CameraProfileStore { Profiles = merged };
            PersistProfilesToBothLocations();
        }
    }

    private static void MergeProfilesFromFile(string path, Dictionary<string, string> target)
    {
        try
        {
            if (!File.Exists(path)) return;
            string content = File.ReadAllText(path, Encoding.UTF8);
            var loaded = JsonSerializer.Deserialize<CameraProfileStore>(content, JsonOptions);
            if (loaded?.Profiles != null)
            {
                foreach (var kv in loaded.Profiles)
                {
                    if (!string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
                    {
                        target[kv.Key.Trim()] = kv.Value.Trim();
                    }
                }
            }
        }
        catch { }
    }

    private void PersistProfilesToBothLocations()
    {
        try
        {
            string json = JsonSerializer.Serialize(_profileStore, JsonOptions);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(_primaryProfilesPath)!);
            File.WriteAllText(_primaryProfilesPath, json, Encoding.UTF8);
            File.WriteAllText(_localExeProfilesPath, json, Encoding.UTF8);
        }
        catch { }
    }

    /// <summary>
    /// Lưu hoặc cập nhật ánh xạ BodySerialNumber -> Tên thợ vào cả %LocalAppData%\PhotoIngestTool\camera_profiles.json và thư mục App.
    /// </summary>
    public void SaveOrUpdateCameraProfile(string serialNumber, string photographerName)
    {
        if (string.IsNullOrWhiteSpace(serialNumber) || string.IsNullOrWhiteSpace(photographerName))
        {
            return;
        }

        lock (_syncLock)
        {
            _profileStore.Profiles[serialNumber.Trim()] = photographerName.Trim();
            PersistProfilesToBothLocations();
        }
    }

    /// <summary>
    /// Tra cứu tên thợ phụ trách theo BodySerialNumber.
    /// </summary>
    public string? LookupPhotographerBySerial(string? serialNumber)
    {
        if (string.IsNullOrWhiteSpace(serialNumber))
        {
            return null;
        }

        lock (_syncLock)
        {
            return _profileStore.Profiles.TryGetValue(serialNumber.Trim(), out string? name)
                ? name
                : null;
        }
    }

    public IReadOnlyDictionary<string, string> GetAllProfiles()
    {
        lock (_syncLock)
        {
            return new Dictionary<string, string>(_profileStore.Profiles, StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Đọc nhanh EXIF từ file RAW đầu tiên trong danh sách file trên thẻ nhớ
    /// (nếu không có RAW thì lấy file ảnh/video đầu tiên).
    /// </summary>
    public async Task<CameraExifInfo> ExtractFirstFileExifAsync(
        IReadOnlyList<string> mediaFiles,
        CancellationToken cancellationToken = default)
    {
        if (mediaFiles == null || mediaFiles.Count == 0)
        {
            return new CameraExifInfo();
        }

        string targetFile = mediaFiles.FirstOrDefault(
            f => DriveWatcherService.RawExtensions.Contains(Path.GetExtension(f)))
            ?? mediaFiles[0];

        return await Task.Run(() => ExtractExifFromFile(targetFile), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Trích xuất chi tiết Make, Model, BodySerialNumber, DateTimeOriginal từ 1 file ảnh.
    /// </summary>
    public CameraExifInfo ExtractExifFromFile(string filePath)
    {
        var result = new CameraExifInfo
        {
            SampleFilePath = filePath
        };

        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return result;
        }

        try
        {
            using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.SequentialScan);

            var directories = ImageMetadataReader.ReadMetadata(stream);

            var ifd0 = directories.OfType<ExifIfd0Directory>().FirstOrDefault();
            var subIfd = directories.OfType<ExifSubIfdDirectory>().FirstOrDefault();

            string? make = ifd0?.GetDescription(ExifDirectoryBase.TagMake)?.Trim();
            string? model = ifd0?.GetDescription(ExifDirectoryBase.TagModel)?.Trim();

            if (!string.IsNullOrWhiteSpace(make))
                result.Make = make;

            if (!string.IsNullOrWhiteSpace(model))
                result.Model = model;

            const int TagDngCameraSerialNumber = 0xC62F;
            string? serial = subIfd?.GetDescription(ExifDirectoryBase.TagBodySerialNumber)?.Trim()
                          ?? ifd0?.GetDescription(TagDngCameraSerialNumber)?.Trim()
                          ?? ifd0?.GetDescription(ExifDirectoryBase.TagBodySerialNumber)?.Trim();

            if (string.IsNullOrWhiteSpace(serial))
            {
                serial = FindSerialInMakerNotes(directories);
            }

            if (!string.IsNullOrWhiteSpace(serial))
            {
                result.SerialNumber = serial;
                result.MatchedPhotographerName = LookupPhotographerBySerial(serial);
            }

            if (subIfd != null && subIfd.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out DateTime dtOriginal))
            {
                result.DateTimeOriginal = dtOriginal;
            }
            else if (subIfd != null && subIfd.TryGetDateTime(ExifDirectoryBase.TagDateTimeDigitized, out DateTime dtDigitized))
            {
                result.DateTimeOriginal = dtDigitized;
            }
            else if (ifd0 != null && ifd0.TryGetDateTime(ExifDirectoryBase.TagDateTime, out DateTime dtIfd0))
            {
                result.DateTimeOriginal = dtIfd0;
            }
        }
        catch
        {
        }

        if (!result.DateTimeOriginal.HasValue)
        {
            try
            {
                result.DateTimeOriginal = File.GetLastWriteTime(filePath);
            }
            catch
            {
                result.DateTimeOriginal = DateTime.Now;
            }
        }

        return result;
    }

    private static string? FindSerialInMakerNotes(IEnumerable<MetadataExtractor.Directory> directories)
    {
        foreach (var dir in directories)
        {
            foreach (var tag in dir.Tags)
            {
                if (tag.Name.Contains("Serial Number", StringComparison.OrdinalIgnoreCase) &&
                    !tag.Name.Contains("Lens", StringComparison.OrdinalIgnoreCase))
                {
                    string? val = tag.Description?.Trim();
                    if (!string.IsNullOrWhiteSpace(val) && val != "0")
                    {
                        return val;
                    }
                }
            }
        }
        return null;
    }

    public DateTime GetCaptureTimeForFile(string filePath)
    {
        try
        {
            using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 32 * 1024,
                FileOptions.SequentialScan);

            var directories = ImageMetadataReader.ReadMetadata(stream);
            var subIfd = directories.OfType<ExifSubIfdDirectory>().FirstOrDefault();
            if (subIfd != null && subIfd.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out DateTime dtOriginal))
            {
                return dtOriginal;
            }

            var ifd0 = directories.OfType<ExifIfd0Directory>().FirstOrDefault();
            if (ifd0 != null && ifd0.TryGetDateTime(ExifDirectoryBase.TagDateTime, out DateTime dt))
            {
                return dt;
            }
        }
        catch
        {
        }

        try
        {
            return File.GetLastWriteTime(filePath);
        }
        catch
        {
            return DateTime.Now;
        }
    }

    public async Task<string> CreateTimeSyncSidecarXmpAsync(
        string sourceFilePath,
        string destinationMediaFilePath,
        TimeSpan timeOffset,
        string? photographerName = null,
        string? cameraSerial = null,
        CancellationToken cancellationToken = default)
    {
        DateTime originalTime = GetCaptureTimeForFile(sourceFilePath);
        DateTime syncedTime = originalTime.Add(timeOffset);

        string xmpFilePath = Path.ChangeExtension(destinationMediaFilePath, ".xmp");

        string isoSynced = syncedTime.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture);
        string exifLegacyFormat = syncedTime.ToString("yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture);
        string creatorAttr = !string.IsNullOrWhiteSpace(photographerName)
            ? $"   aux:OwnerName=\"{EscapeXml(photographerName)}\"{Environment.NewLine}"
            : string.Empty;
        string serialAttr = !string.IsNullOrWhiteSpace(cameraSerial)
            ? $"   aux:SerialNumber=\"{EscapeXml(cameraSerial)}\"{Environment.NewLine}"
            : string.Empty;

        string xmpContent =
$@"<?xpacket begin=""﻿"" id=""W5M0MpCehiHzreSzNTczkc9d""?>
<x:xmpmeta xmlns:x=""adobe:ns:meta/"" x:xmptk=""PhotoIngestTool 1.0"">
 <rdf:RDF xmlns:rdf=""http://www.w3.org/1999/02/22-rdf-syntax-ns#"">
  <rdf:Description rdf:about=""""
    xmlns:exif=""http://ns.adobe.com/exif/1.0/""
    xmlns:xmp=""http://ns.adobe.com/xap/1.0/""
    xmlns:photoshop=""http://ns.adobe.com/photoshop/1.0/""
    xmlns:aux=""http://ns.adobe.com/exif/1.0/aux/""
   exif:DateTimeOriginal=""{isoSynced}""
   exif:DateTimeDigitized=""{isoSynced}""
   photoshop:DateCreated=""{isoSynced}""
   xmp:CreateDate=""{isoSynced}""
   xmp:MetadataDate=""{DateTime.Now.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture)}""
{creatorAttr}{serialAttr}   aux:TimeSyncOffsetSeconds=""{timeOffset.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture)}""
   aux:OriginalExifTime=""{exifLegacyFormat}""/>
 </rdf:RDF>
</x:xmpmeta>
<?xpacket end=""w""?>";

        await File.WriteAllTextAsync(xmpFilePath, xmpContent, new UTF8Encoding(false), cancellationToken)
            .ConfigureAwait(false);

        return xmpFilePath;
    }

    private static string EscapeXml(string input)
    {
        return input
            .Replace("&", "&amp;")
            .Replace("\"", "&quot;")
            .Replace("'", "&apos;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;");
    }
}
