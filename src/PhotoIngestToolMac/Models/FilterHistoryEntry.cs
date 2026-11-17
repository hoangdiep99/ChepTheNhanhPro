using System;
using System.Collections.Generic;

namespace PhotoIngestTool.Models;

/// <summary>
/// Bản ghi lịch sử 1 lần lọc ảnh theo mã (lưu trong filter_history.json).
/// </summary>
public class FilterHistoryEntry
{
    public Guid FilterId { get; set; } = Guid.NewGuid();
    public DateTime FilteredAt { get; set; } = DateTime.Now;
    public string SourceDirectory { get; set; } = string.Empty;
    public string DestinationDirectory { get; set; } = string.Empty;
    public string EffectiveDestinationFolder { get; set; } = string.Empty;
    public string FileTypes { get; set; } = string.Empty;
    public string RawSequenceText { get; set; } = string.Empty;
    public int RequestedCodesCount { get; set; }
    public int FoundCodesCount { get; set; }
    public int MissingCodesCount { get; set; }
    public List<string> MissingCodes { get; set; } = new();
    public int FilteredFilesCount { get; set; }
    public double ElapsedSeconds { get; set; }
    public bool HasMissingCodes => MissingCodesCount > 0;
}

public class FilterHistoryStore
{
    public List<FilterHistoryEntry> Entries { get; set; } = new();
}
