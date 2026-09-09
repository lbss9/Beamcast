using System.Globalization;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.Resources.Core;

namespace Beamcast;

public partial class App : Application
{
    public static MainWindow? Main { get; private set; }

    public App()
    {
        // Three nets: XAML (UI thread), the runtime (any thread) and faulted tasks nobody awaited.
        // Everything lands in crash.log (always) and diag.log (when enabled) with the full chain
        // of inner exceptions and the stack, which is what a bug report needs.
        UnhandledException += (_, e) => Diag.RecordCrash("xaml", e.Exception, e.Message);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Diag.RecordCrash("runtime", e.ExceptionObject as Exception, e.IsTerminating ? "terminating" : "non-terminating");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Diag.RecordCrash("task", e.Exception, "unobserved (app keeps running)");
            e.SetObserved();
        };
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var settings = SettingsStore.Load();
        if (!SettingsStore.Exists())
            SettingsStore.Save(settings);

        ApplyCulture(settings.Language);
        Diag.Log($"startup: Beamcast {AppInfo.Version} on {Environment.OSVersion.VersionString}, {Environment.ProcessorCount} cores, {(Environment.Is64BitProcess ? "x64" : "x86")}");
        Diag.NoteCrashLogAtStartup();
        // Ask Windows early for capture without the coloured border, so the first capture is clean.
        _ = Capture.CaptureAccess.EnsureBorderlessAsync();
        Main = new MainWindow();
        Main.ApplyTheme(settings.Theme);
        Main.Activate();

        if (settings.CheckUpdatesOnLaunch)
            _ = CheckUpdatesOnLaunchAsync();
    }

    private static async Task CheckUpdatesOnLaunchAsync()
    {
        var check = await UpdateService.CheckAsync();
        if (check.Kind is not (UpdateCheckKind.Available or UpdateCheckKind.ReadyToRestart) || check.Offer is null)
            return;
        Main?.DispatcherQueue.TryEnqueue(() => Main.NotifyUpdate(check.Offer));
    }

    public static void ApplyCulture(string language)
    {
        try
        {
            var resolved = AppLanguage.Resolve(language);
            var culture = new CultureInfo(resolved);
            CultureInfo.DefaultThreadCurrentCulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            Thread.CurrentThread.CurrentCulture = culture;
            Thread.CurrentThread.CurrentUICulture = culture;
            ResourceContext.SetGlobalQualifierValue("Language", resolved);
        }
        catch { }
    }
}
