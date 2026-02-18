using System.Reflection;

namespace BoltMacro;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        ApplicationConfiguration.Initialize();

        // Crash visibility for MVP.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            try { MessageBox.Show(e.Exception.ToString(), "UI Thread Exception"); } catch { }
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                try { MessageBox.Show(ex.ToString(), "Unhandled Exception"); } catch { }
            }
        };

        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string appDir = Path.Combine(appData, "BoltMacro");
        Directory.CreateDirectory(appDir);

        string dbPath = Path.Combine(appDir, "bolt.db");
        string settingsPath = Path.Combine(appDir, "settings.json");

        var storage = new Storage(dbPath);
        var settingsStore = new SettingsStore(settingsPath);
        var settings = settingsStore.Load();
        var timeline = new TimelineService(storage);
        var lease = new InputLeaseManager(timeline);
        var recorder = new RecorderService(timeline);
        var player = new PlaybackService(timeline);
        var roiMonitor = new RoiMonitorService(timeline);

        using var controller = new Controller(storage, timeline, lease, recorder, player, roiMonitor);
        controller.InitializeRuntimeRules();

        Application.Run(new MainForm(controller, storage, timeline, settingsStore, settings));
    }
}
