using System;
using Avalonia;

namespace PhotoIngestTool;

internal sealed class Program
{
    public static string? StartupSourcePath { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        if (args != null && args.Length > 0 && !string.IsNullOrWhiteSpace(args[0]))
        {
            StartupSourcePath = args[0].Trim('"');
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args ?? Array.Empty<string>());
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
