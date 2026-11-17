using System;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using PhotoIngestTool.Models;

namespace PhotoIngestTool.Services;

/// <summary>
/// Dịch vụ tự động kiểm tra bản cập nhật mới từ kho lưu trữ GitHub của tác giả Hoàng Điệp (hoangdiep99).
/// </summary>
public class UpdateService
{
    public const string CurrentVersion = "1.7.0";
    public const string GitHubRepoUrl = "https://github.com/hoangdiep99/ChepTheNhanhPro";
    public const string VersionCheckUrl = "https://raw.githubusercontent.com/hoangdiep99/ChepTheNhanhPro/main/version.json";
    public const string ReleasesApiUrl = "https://api.github.com/repos/hoangdiep99/ChepTheNhanhPro/releases/latest";

    private static readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(6)
    };

    static UpdateService()
    {
        try
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("ChepTheNhanhPro-Updater/1.0");
        }
        catch { }
    }

    /// <summary>
    /// Kiểm tra phiên bản mới từ GitHub (ưu tiên file raw version.json, fallback sang GitHub Releases API).
    /// </summary>
    public async Task<AppUpdateInfo?> CheckForUpdateAsync()
    {
        try
        {
            // 1. Thử đọc từ version.json (file tùy biến trực tiếp của tác giả trên nhánh main)
            try
            {
                string urlWithCacheBuster = $"{VersionCheckUrl}?t={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
                using var request = new HttpRequestMessage(HttpMethod.Get, urlWithCacheBuster);
                request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true, NoStore = true };

                using var response = await _httpClient.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    string json = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    string latestVer = root.TryGetProperty("latestVersion", out var v) ? v.GetString() ?? CurrentVersion : CurrentVersion;
                    string releaseDate = root.TryGetProperty("releaseDate", out var rd) ? rd.GetString() ?? "" : "";
                    string title = root.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                    string changelog = root.TryGetProperty("changelog", out var cl) ? cl.GetString() ?? "" : "";
                    string winUrl = root.TryGetProperty("downloadUrlWindows", out var dw) ? dw.GetString() ?? "" : "";
                    string macUrl = root.TryGetProperty("downloadUrlMac", out var dm) ? dm.GetString() ?? "" : "";

                    if (string.IsNullOrWhiteSpace(winUrl)) winUrl = $"{GitHubRepoUrl}/releases/latest";
                    if (string.IsNullOrWhiteSpace(macUrl)) macUrl = $"{GitHubRepoUrl}/releases/latest";

                    bool isNewer = CompareVersions(latestVer, CurrentVersion) > 0;

                    return new AppUpdateInfo
                    {
                        LatestVersion = latestVer,
                        ReleaseDate = releaseDate,
                        Title = string.IsNullOrWhiteSpace(title) ? $"Phiên bản mới v{latestVer}" : title,
                        Changelog = changelog,
                        DownloadUrlWindows = winUrl,
                        DownloadUrlMac = macUrl,
                        IsUpdateAvailable = isNewer
                    };
                }
            }
            catch { }

            // 2. Fallback kiểm tra qua GitHub Releases API
            try
            {
                string releasesUrlWithCacheBuster = $"{ReleasesApiUrl}?t={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
                using var relRequest = new HttpRequestMessage(HttpMethod.Get, releasesUrlWithCacheBuster);
                relRequest.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true, NoStore = true };

                using var response = await _httpClient.SendAsync(relRequest);
                if (response.IsSuccessStatusCode)
                {
                    string json = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    string tagName = root.TryGetProperty("tag_name", out var tn) ? tn.GetString() ?? CurrentVersion : CurrentVersion;
                    string cleanVer = tagName.TrimStart('v', 'V').Trim();
                    string name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    string body = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
                    string htmlUrl = root.TryGetProperty("html_url", out var hu) ? hu.GetString() ?? "" : $"{GitHubRepoUrl}/releases/latest";

                    bool isNewer = CompareVersions(cleanVer, CurrentVersion) > 0;

                    return new AppUpdateInfo
                    {
                        LatestVersion = cleanVer,
                        ReleaseDate = DateTime.Now.ToString("dd/MM/yyyy"),
                        Title = string.IsNullOrWhiteSpace(name) ? $"Bản cập nhật v{cleanVer}" : name,
                        Changelog = body,
                        DownloadUrlWindows = htmlUrl,
                        DownloadUrlMac = htmlUrl,
                        IsUpdateAvailable = isNewer
                    };
                }
            }
            catch { }

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// So sánh 2 chuỗi version ngữ nghĩa (ví dụ "1.4.0" > "1.0.0").
    /// Trả về > 0 nếu v1 lớn hơn v2; 0 nếu bằng nhau; < 0 nếu v1 nhỏ hơn v2.
    /// </summary>
    public static int CompareVersions(string v1, string v2)
    {
        try
        {
            string clean1 = v1.TrimStart('v', 'V').Trim();
            string clean2 = v2.TrimStart('v', 'V').Trim();

            if (Version.TryParse(clean1, out var ver1) && Version.TryParse(clean2, out var ver2))
            {
                return ver1.CompareTo(ver2);
            }

            var parts1 = clean1.Split('.');
            var parts2 = clean2.Split('.');
            int len = Math.Max(parts1.Length, parts2.Length);

            for (int i = 0; i < len; i++)
            {
                int num1 = i < parts1.Length && int.TryParse(parts1[i], out int n1) ? n1 : 0;
                int num2 = i < parts2.Length && int.TryParse(parts2[i], out int n2) ? n2 : 0;
                if (num1 != num2) return num1.CompareTo(num2);
            }

            return 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Phân giải link tải trực tiếp file .zip từ danh sách assets của release mới nhất trên GitHub
    /// </summary>
    public async Task<string> ResolveMacZipUrlAsync(string fallbackUrl)
    {
        try
        {
            using var response = await _httpClient.GetAsync(ReleasesApiUrl);
            if (response.IsSuccessStatusCode)
            {
                string json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                {
                    foreach (var asset in assets.EnumerateArray())
                    {
                        string name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                        if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && name.Contains("AppleSilicon", StringComparison.OrdinalIgnoreCase))
                        {
                            return asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? fallbackUrl : fallbackUrl;
                        }
                    }
                }
            }
        }
        catch { }
        return fallbackUrl;
    }
}
