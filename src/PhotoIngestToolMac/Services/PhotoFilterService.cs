using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PhotoIngestTool.Services;

public class PhotoFilterRequest
{
    public string SourceDirectory { get; set; } = string.Empty;
    public string DestinationDirectory { get; set; } = string.Empty;
    public string SequenceText { get; set; } = string.Empty;
    public string FileTypes { get; set; } = string.Empty;
    public bool CreateSubfolder { get; set; } = true;
    public string SubfolderName { get; set; } = string.Empty;
}

public class PhotoFilterProgress
{
    public int ProcessedCount { get; set; }
    public int TotalMatchingCandidates { get; set; }
    public double ProgressPercentage { get; set; }
    public string StatusMessage { get; set; } = string.Empty;
    public string? LogMessage { get; set; }
    public bool IsError { get; set; }
}

public class PhotoFilterResult
{
    public int SuccessCount => FilteredFiles.Count;
    public int SkippedCount => SkippedFiles.Count;
    public int ErrorCount => ErrorFiles.Count;
    public List<string> FilteredFiles { get; } = new();
    public List<string> SkippedFiles { get; } = new();
    public List<string> ErrorFiles { get; } = new();
    public double ElapsedSeconds { get; set; }
    public string EffectiveDestinationFolder { get; set; } = string.Empty;
    public bool WasCancelled { get; set; }

    // === ĐỐI SOÁT & BÁO CÁO MÃ THIẾU (MISSING INSPECTOR) ===
    public List<string> RequestedCodes { get; } = new();
    public List<string> FoundCodes { get; } = new();
    public List<string> MissingCodes { get; } = new();
    public bool HasMissingCodes => MissingCodes.Count > 0;
}

public class RequestedCodeItem
{
    public string OriginalCode { get; set; } = string.Empty;
    public List<string> SearchTokens { get; set; } = new();
    public bool IsNumeric { get; set; }
    public string NumericDigits { get; set; } = string.Empty;
    public bool IsMatched { get; set; }
    public List<string> MatchedFiles { get; } = new();
}

public class PhotoFilterService
{
    private static readonly HashSet<string> DefaultImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff",
        ".nef", ".cr2", ".cr3", ".arw", ".raf", ".dng", ".raw", ".orf", ".rw2"
    };

    /// <summary>
    /// Phân tích danh sách mã do người dùng nhập hoặc copy từ Zalo/Excel thành danh sách RequestedCodeItem.
    /// Tự động làm sạch văn bản trò chuyện thừa (chỉ giữ lại các mã chứa số), hỗ trợ mã số ngắn, đệm số 0 và mã máy ảnh.
    /// </summary>
    public static List<RequestedCodeItem> ParseRequestedCodes(string sequenceText)
    {
        var list = new List<RequestedCodeItem>();
        if (string.IsNullOrWhiteSpace(sequenceText))
            return list;

        // Tách theo các ký tự phân tách phổ biến: xuống dòng, phẩy, chấm phẩy, tab, gạch đứng
        var rawTokens = Regex.Split(sequenceText, @"[\r\n,;\t|]+");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawChunk in rawTokens)
        {
            if (string.IsNullOrWhiteSpace(rawChunk)) continue;

            // Nếu chunk chứa nhiều số hoặc mã cách nhau bởi khoảng trắng: VD "12 45 98 203"
            var subParts = Regex.Split(rawChunk.Trim(), @"\s+");
            foreach (var part in subParts)
            {
                // Làm sạch ký tự bao quanh như ngoặc, dấu hai chấm, dấu gạch
                string cleaned = part.Trim(' ', ':', ';', ',', '(', ')', '[', ']', '{', '}', '"', '\'', '-', '+', '#');
                if (string.IsNullOrWhiteSpace(cleaned)) continue;

                // Bỏ qua các từ thuần chữ không chứa chữ số (VD: "chị", "ảnh", "chọn", "nhe", "zalo")
                if (!cleaned.Any(char.IsDigit)) continue;

                if (!seen.Add(cleaned)) continue;

                var item = new RequestedCodeItem
                {
                    OriginalCode = cleaned
                };

                if (cleaned.All(char.IsDigit))
                {
                    item.IsNumeric = true;
                    item.NumericDigits = cleaned.TrimStart('0');
                    if (string.IsNullOrEmpty(item.NumericDigits))
                    {
                        item.NumericDigits = "0";
                    }

                    item.SearchTokens.Add(cleaned);
                    if (cleaned.Length < 4)
                    {
                        item.SearchTokens.Add(cleaned.PadLeft(4, '0'));
                    }
                    if (cleaned.Length < 5)
                    {
                        item.SearchTokens.Add(cleaned.PadLeft(5, '0'));
                    }
                    if (cleaned.Length < 3)
                    {
                        item.SearchTokens.Add(cleaned.PadLeft(3, '0'));
                    }
                }
                else
                {
                    item.IsNumeric = false;
                    item.SearchTokens.Add(cleaned);

                    string withoutExt = Path.GetFileNameWithoutExtension(cleaned);
                    if (!string.IsNullOrEmpty(withoutExt) && !withoutExt.Equals(cleaned, StringComparison.OrdinalIgnoreCase))
                    {
                        item.SearchTokens.Add(withoutExt);
                    }

                    var digitsMatch = Regex.Match(cleaned, @"\d+");
                    if (digitsMatch.Success)
                    {
                        item.NumericDigits = digitsMatch.Value;
                        item.SearchTokens.Add(digitsMatch.Value);
                        if (digitsMatch.Value.Length < 4)
                        {
                            item.SearchTokens.Add(digitsMatch.Value.PadLeft(4, '0'));
                        }
                    }
                }

                item.SearchTokens = item.SearchTokens.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                list.Add(item);
            }
        }

        return list;
    }

    /// <summary>
    /// Kiểm tra xem tên file có khớp với mã được yêu cầu hay không.
    /// Đảm bảo không nhận nhầm số ngắn (VD: mã "12" không bị nhận nhầm với "DSC_1298").
    /// </summary>
    public static bool MatchesFile(RequestedCodeItem item, string fileName)
    {
        string fileNameWithoutExt = Path.GetFileNameWithoutExtension(fileName);

        // 1. Kiểm tra khớp token trực tiếp (độ dài >= 4, VD "0012", "1234", "DSC_0012")
        foreach (var token in item.SearchTokens)
        {
            if (token.Length >= 4)
            {
                if (fileName.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
        }

        // 2. Nếu là mã thuần số
        if (item.IsNumeric)
        {
            // 2a. Kiểm tra token đệm 0 (VD: "0012", "0105", "00012")
            foreach (var token in item.SearchTokens)
            {
                if (token.Length >= 3 && fileName.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            // 2b. Kiểm tra ranh giới từ/số chính xác với tên file (VD: "12.jpg", "_12.jpg", "-12.jpg", " 12.jpg")
            string pattern = $@"(^|[^\d]){Regex.Escape(item.OriginalCode)}([^\d]|$)";
            if (Regex.IsMatch(fileNameWithoutExt, pattern, RegexOptions.IgnoreCase))
            {
                return true;
            }

            if (!string.IsNullOrEmpty(item.NumericDigits))
            {
                string numPattern = $@"(^|[^\d]){Regex.Escape(item.NumericDigits)}([^\d]|$)";
                if (Regex.IsMatch(fileNameWithoutExt, numPattern, RegexOptions.IgnoreCase))
                {
                    return true;
                }
            }
        }
        else
        {
            // 3. Mã chữ số: kiểm tra trực tiếp OriginalCode
            if (fileName.IndexOf(item.OriginalCode, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            foreach (var token in item.SearchTokens)
            {
                if (token.Length >= 3 && fileName.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
        }

        return false;
    }

    public static List<string>? ParseValidExtensions(string fileTypes)
    {
        if (string.IsNullOrWhiteSpace(fileTypes))
            return null; // Chấp nhận mọi định dạng

        var parts = Regex.Split(fileTypes.Trim(), @"[,\s|]+");
        var list = new List<string>();
        foreach (var part in parts)
        {
            string clean = part.Trim().ToLowerInvariant().TrimStart('.');
            if (!string.IsNullOrEmpty(clean))
            {
                list.Add("." + clean);
            }
        }

        return list.Count > 0 ? list : null;
    }

    public static string SanitizeFolderName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return $"Auto loc anh {DateTime.Now:dd-MM-yyyy}";

        var invalidChars = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder();
        foreach (char c in name)
        {
            if (!invalidChars.Contains(c))
                sb.Append(c);
        }

        string result = sb.ToString().Trim();
        return string.IsNullOrEmpty(result) ? $"Auto loc anh {DateTime.Now:dd-MM-yyyy}" : result;
    }

    public async Task<PhotoFilterResult> FilterImagesAsync(
        PhotoFilterRequest request,
        IProgress<PhotoFilterProgress>? progress,
        CancellationToken cancellationToken)
    {
        var result = new PhotoFilterResult();
        var sw = Stopwatch.StartNew();

        string sourceDir = request.SourceDirectory?.Trim() ?? string.Empty;
        if (!Directory.Exists(sourceDir))
        {
            throw new DirectoryNotFoundException($"Thư mục nguồn không tồn tại: {sourceDir}");
        }

        string destDir = request.DestinationDirectory?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(destDir))
        {
            destDir = sourceDir;
        }

        string targetFolder = destDir;
        if (request.CreateSubfolder)
        {
            string subName = SanitizeFolderName(request.SubfolderName);
            targetFolder = Path.Combine(destDir, subName);
        }

        Directory.CreateDirectory(targetFolder);
        result.EffectiveDestinationFolder = targetFolder;

        var requestedCodes = ParseRequestedCodes(request.SequenceText);
        bool hasRequestedCodes = requestedCodes.Count > 0;
        var validExtensions = ParseValidExtensions(request.FileTypes);

        // Lấy danh sách toàn bộ file trong thư mục nguồn
        var allSourceFiles = await Task.Run(() =>
        {
            try
            {
                return Directory.GetFiles(sourceDir, "*", SearchOption.TopDirectoryOnly)
                    .Select(Path.GetFileName)
                    .Where(f => !string.IsNullOrEmpty(f))
                    .Cast<string>()
                    .ToList();
            }
            catch
            {
                return new List<string>();
            }
        }, cancellationToken);

        int totalFiles = allSourceFiles.Count;
        if (totalFiles == 0)
        {
            result.ElapsedSeconds = sw.Elapsed.TotalSeconds;
            return result;
        }

        int processed = 0;
        var lockObj = new object();

        // Xử lý song song đa luồng siêu tốc
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 8),
            CancellationToken = cancellationToken
        };

        try
        {
            await Parallel.ForEachAsync(allSourceFiles, parallelOptions, async (fileName, ct) =>
            {
                ct.ThrowIfCancellationRequested();

                string srcPath = Path.Combine(sourceDir, fileName);
                string dstPath = Path.Combine(targetFolder, fileName);
                string ext = Path.GetExtension(fileName);

                // 1. Kiểm tra định dạng đuôi file
                if (validExtensions != null && validExtensions.Count > 0)
                {
                    if (!validExtensions.Any(ve => ext.Equals(ve, StringComparison.OrdinalIgnoreCase)))
                    {
                        int c = Interlocked.Increment(ref processed);
                        ReportSkip(fileName, "Không khớp định dạng đuôi file", c, totalFiles, sw.Elapsed.TotalSeconds, progress);
                        return;
                    }
                }
                else
                {
                    // Nếu để trống thì chỉ nhận các file ảnh hoặc RAW thông dụng
                    if (!DefaultImageExtensions.Contains(ext))
                    {
                        int c = Interlocked.Increment(ref processed);
                        return;
                    }
                }

                // 2. Kiểm tra mã ảnh / số thứ tự
                List<RequestedCodeItem>? matchedItems = null;
                if (hasRequestedCodes)
                {
                    matchedItems = new List<RequestedCodeItem>();
                    foreach (var codeItem in requestedCodes)
                    {
                        if (MatchesFile(codeItem, fileName))
                        {
                            matchedItems.Add(codeItem);
                        }
                    }

                    if (matchedItems.Count == 0)
                    {
                        int c = Interlocked.Increment(ref processed);
                        ReportSkip(fileName, "Không khớp mã ảnh", c, totalFiles, sw.Elapsed.TotalSeconds, progress);
                        return;
                    }
                }

                // Ghi nhận mã ảnh đã tìm thấy
                if (matchedItems != null)
                {
                    lock (lockObj)
                    {
                        foreach (var m in matchedItems)
                        {
                            m.IsMatched = true;
                            m.MatchedFiles.Add(fileName);
                        }
                    }
                }

                // 3. Kiểm tra file đã tồn tại ở thư mục đích
                if (File.Exists(dstPath))
                {
                    int c = Interlocked.Increment(ref processed);
                    lock (lockObj) { result.SkippedFiles.Add($"{fileName} (Đã có sẵn tại thư mục đích)"); }
                    ReportSkip(fileName, "File đã có sẵn tại thư mục đích", c, totalFiles, sw.Elapsed.TotalSeconds, progress);
                    return;
                }

                // 4. Tiến hành copy file
                try
                {
                    await Task.Run(() => File.Copy(srcPath, dstPath, overwrite: false), ct);

                    lock (lockObj) { result.FilteredFiles.Add(fileName); }
                    int c = Interlocked.Increment(ref processed);

                    progress?.Report(new PhotoFilterProgress
                    {
                        ProcessedCount = c,
                        TotalMatchingCandidates = totalFiles,
                        ProgressPercentage = (double)c / totalFiles * 100.0,
                        StatusMessage = $"Đang xử lý {c}/{totalFiles} file ({sw.Elapsed.TotalSeconds:F1}s)",
                        LogMessage = $"Đã lọc: {fileName}",
                        IsError = false
                    });
                }
                catch (Exception ex)
                {
                    lock (lockObj) { result.ErrorFiles.Add($"{fileName}: {ex.Message}"); }
                    int c = Interlocked.Increment(ref processed);

                    progress?.Report(new PhotoFilterProgress
                    {
                        ProcessedCount = c,
                        TotalMatchingCandidates = totalFiles,
                        ProgressPercentage = (double)c / totalFiles * 100.0,
                        StatusMessage = $"Lỗi xử lý file {fileName}",
                        LogMessage = $"Lỗi sao chép {fileName}: {ex.Message}",
                        IsError = true
                    });
                }
            });
        }
        catch (OperationCanceledException)
        {
            result.WasCancelled = true;
        }

        result.ElapsedSeconds = sw.Elapsed.TotalSeconds;

        // Tổng kết kết quả Đối soát mã thiếu
        if (hasRequestedCodes)
        {
            foreach (var codeItem in requestedCodes)
            {
                result.RequestedCodes.Add(codeItem.OriginalCode);
                if (codeItem.IsMatched)
                {
                    result.FoundCodes.Add(codeItem.OriginalCode);
                }
                else
                {
                    result.MissingCodes.Add(codeItem.OriginalCode);
                }
            }
        }

        // Ghi log chi tiết vào file log_loc_anh.txt
        try
        {
            string logPath = Path.Combine(targetFolder, "log_loc_anh.txt");
            var logSb = new StringBuilder();
            logSb.AppendLine(new string('=', 50));
            logSb.AppendLine($"Thời gian lọc: {DateTime.Now:dd-MM-yyyy HH:mm:ss}");
            logSb.AppendLine($"Nguồn: {sourceDir}");
            logSb.AppendLine($"Đích : {targetFolder}");
            logSb.AppendLine($"Tìm thấy và đã copy: {result.FilteredFiles.Count} ảnh ({result.ElapsedSeconds:F2} giây)");

            if (result.RequestedCodes.Count > 0)
            {
                logSb.AppendLine(new string('-', 45));
                logSb.AppendLine("📋 BÁO CÁO ĐỐI SOÁT MÃ ẢNH (MISSING INSPECTOR):");
                logSb.AppendLine($"- Tổng số mã yêu cầu: {result.RequestedCodes.Count} mã");
                logSb.AppendLine($"- Đã tìm thấy & lọc: {result.FoundCodes.Count} mã ({result.FilteredFiles.Count} file)");
                if (result.MissingCodes.Count > 0)
                {
                    logSb.AppendLine($"- ⚠️ MÃ THIẾU / KHÔNG TÌM THẤY ({result.MissingCodes.Count} mã):");
                    logSb.AppendLine($"  {string.Join(", ", result.MissingCodes)}");
                }
                else
                {
                    logSb.AppendLine("- ✅ Tìm thấy đầy đủ 100% mã ảnh yêu cầu trong thư mục nguồn!");
                }
                logSb.AppendLine(new string('-', 45));
            }

            logSb.AppendLine("Danh sách ảnh đã lọc:");
            foreach (var f in result.FilteredFiles)
            {
                logSb.AppendLine($"- {f}");
            }

            if (result.SkippedFiles.Count > 0)
            {
                logSb.AppendLine($"Bỏ qua {result.SkippedFiles.Count} file:");
                foreach (var s in result.SkippedFiles)
                {
                    logSb.AppendLine($"- {s}");
                }
            }

            if (result.ErrorFiles.Count > 0)
            {
                logSb.AppendLine($"Danh sách lỗi ({result.ErrorFiles.Count}):");
                foreach (var err in result.ErrorFiles)
                {
                    logSb.AppendLine($"- {err}");
                }
            }

            File.AppendAllText(logPath, logSb.ToString(), Encoding.UTF8);
        }
        catch { }

        return result;
    }

    private static void ReportSkip(
        string fileName,
        string reason,
        int currentCount,
        int total,
        double elapsedSec,
        IProgress<PhotoFilterProgress>? progress)
    {
        progress?.Report(new PhotoFilterProgress
        {
            ProcessedCount = currentCount,
            TotalMatchingCandidates = total,
            ProgressPercentage = (double)currentCount / total * 100.0,
            StatusMessage = $"Đang quét {currentCount}/{total} file ({elapsedSec:F1}s)",
            LogMessage = null, // Không spam log cho file bị skip do không khớp mã
            IsError = false
        });
    }
}
