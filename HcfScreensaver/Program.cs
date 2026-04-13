using HcfScreensaver.Forms;
using HcfScreensaver.Services;

namespace HcfScreensaver;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        var settingsService = new SettingsService();
        var mode = ParseMode(args, out var previewHandle);

        switch (mode)
        {
            case ScreensaverMode.Run:
                RunScreensaver(settingsService, previewHandle);
                break;
            case ScreensaverMode.Preview:
                RunPreview(settingsService, previewHandle);
                break;
            case ScreensaverMode.Configure:
            default:
                ShowConfiguration(settingsService);
                break;
        }
    }

    private static void ShowConfiguration(SettingsService settingsService)
    {
        var settings = settingsService.Load();
        using var configForm = new ConfigForm(settingsService, settings);
        configForm.ShowDialog();
    }

    private static void RunScreensaver(SettingsService settingsService, IntPtr previewHandle)
    {
        var settings = settingsService.Load();
        using var context = new ScreensaverApplicationContext(settings, false, previewHandle);
        Application.Run(context);
    }

    private static void RunPreview(SettingsService settingsService, IntPtr previewHandle)
    {
        if (previewHandle == IntPtr.Zero)
        {
            ShowConfiguration(settingsService);
            return;
        }

        var settings = settingsService.Load();
        using var context = new ScreensaverApplicationContext(settings, true, previewHandle);
        Application.Run(context);
    }

    private static ScreensaverMode ParseMode(string[] args, out IntPtr previewHandle)
    {
        previewHandle = IntPtr.Zero;

        if (args.Length == 0)
            return ScreensaverMode.Configure;

        var arg = args[0].Trim().ToLowerInvariant();

        if (arg.StartsWith("/s") || arg.StartsWith("-s"))
            return ScreensaverMode.Run;

        if (arg.StartsWith("/c") || arg.StartsWith("-c"))
            return ScreensaverMode.Configure;

        if (arg.StartsWith("/p") || arg.StartsWith("-p"))
        {
            var handleValue = string.Empty;
            var split = arg.Split(':', 2, StringSplitOptions.TrimEntries);
            if (split.Length == 2)
                handleValue = split[1];
            else if (args.Length > 1)
                handleValue = args[1];

            if (long.TryParse(handleValue, out var parsedHandle))
                previewHandle = new IntPtr(parsedHandle);

            return ScreensaverMode.Preview;
        }

        return ScreensaverMode.Configure;
    }
}

internal enum ScreensaverMode
{
    Configure,
    Run,
    Preview
}
