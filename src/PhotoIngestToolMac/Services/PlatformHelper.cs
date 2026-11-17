using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace PhotoIngestTool.Services;

/// <summary>
/// Hỗ trợ các thao tác hệ thống đặc thù trên macOS (Finder, TextEdit, afplay, LaunchAgents) và Windows.
/// </summary>
public static class PlatformHelper
{
    private const string MacLaunchAgentLabel = "com.hoangdiep.chepthenhanhpro";

    private static string GetMacLaunchAgentPlistPath()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, "Library", "LaunchAgents", $"{MacLaunchAgentLabel}.plist");
    }

    public static void OpenFolder(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            return;

        try
        {
            if (OperatingSystem.IsMacOS())
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "/usr/bin/open",
                    Arguments = $"\"{folderPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
            }
            else if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folderPath}\"") { UseShellExecute = true });
            }
            else
            {
                Process.Start(new ProcessStartInfo("xdg-open", $"\"{folderPath}\"") { UseShellExecute = false });
            }
        }
        catch { }
    }

    public static void OpenTextFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return;

        try
        {
            if (OperatingSystem.IsMacOS())
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "/usr/bin/open",
                    Arguments = $"-t \"{filePath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
            }
            else if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo("notepad.exe", $"\"{filePath}\"") { UseShellExecute = true });
            }
            else
            {
                Process.Start(new ProcessStartInfo("xdg-open", $"\"{filePath}\"") { UseShellExecute = false });
            }
        }
        catch { }
    }

    public static void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return;

        try
        {
            if (OperatingSystem.IsMacOS())
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "/usr/bin/open",
                    Arguments = $"\"{url}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
            }
            else
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
        }
        catch { }
    }

    public static void PlayCompletionSound()
    {
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                string soundFile = "/System/Library/Sounds/Glass.aiff";
                if (!File.Exists(soundFile))
                    soundFile = "/System/Library/Sounds/Ping.aiff";

                if (File.Exists(soundFile))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "/usr/bin/afplay",
                        Arguments = $"\"{soundFile}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                }
            }
            else
            {
                Console.Beep();
            }
        }
        catch { }
    }

    public static bool IsStartupEnabled()
    {
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                return File.Exists(GetMacLaunchAgentPlistPath());
            }
        }
        catch { }
        return false;
    }

    public static (bool Success, string Message) SetStartupEnabled(bool enable)
    {
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                string plistPath = GetMacLaunchAgentPlistPath();
                if (enable)
                {
                    string? exePath = Environment.ProcessPath;
                    if (string.IsNullOrWhiteSpace(exePath))
                    {
                        return (false, "Không xác định được đường dẫn thực thi trên macOS.");
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(plistPath)!);
                    string plistXml = $"""
                        <?xml version="1.0" encoding="UTF-8"?>
                        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                        <plist version="1.0">
                        <dict>
                            <key>Label</key>
                            <string>{MacLaunchAgentLabel}</string>
                            <key>ProgramArguments</key>
                            <array>
                                <string>{exePath}</string>
                            </array>
                            <key>RunAtLoad</key>
                            <true/>
                        </dict>
                        </plist>
                        """;
                    File.WriteAllText(plistPath, plistXml, Encoding.UTF8);
                    return (true, "Đã bật tự khởi động cùng macOS (LaunchAgents).");
                }
                else
                {
                    if (File.Exists(plistPath))
                        File.Delete(plistPath);
                    return (true, "Đã tắt tự khởi động cùng macOS.");
                }
            }
            return (true, "Tính năng tự khởi động chỉ áp dụng trên macOS LaunchAgents.");
        }
        catch (Exception ex)
        {
            return (false, $"Lỗi cấu hình khởi động cùng hệ thống: {ex.Message}");
        }
    }
}
