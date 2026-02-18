using System.Text.Json;
using System.IO;
using System.Windows.Forms;

namespace BoltMacro;

public sealed class AppSettings
{
    public float UiFontSize { get; set; } = 10.0f;
    public int UiScalePercent { get; set; } = 100;
    public float DiagnosticsFontSize { get; set; } = 10.0f;
    public bool OcrModuleEnabled { get; set; } = false;
    public bool UiaModuleEnabled { get; set; } = true;
    public bool SmartRulesEnabled { get; set; } = true;
    public bool DiagnosticsModeEnabled { get; set; } = false;
    public string DiagnosticsZoomInKey { get; set; } = nameof(Keys.Add);
    public string DiagnosticsZoomOutKey { get; set; } = nameof(Keys.Subtract);
    public string DiagnosticsPageUpKey { get; set; } = nameof(Keys.PageUp);
    public string DiagnosticsPageDownKey { get; set; } = nameof(Keys.PageDown);
    public string DiagnosticsCloseKey { get; set; } = nameof(Keys.Escape);
    public string DiagnosticsOpenKey { get; set; } = nameof(Keys.F9);
    public string PanicStopKey { get; set; } = $"Ctrl+{nameof(Keys.F12)}";
    public bool TriggerDebugDetails { get; set; } = false;
}

public sealed class SettingsStore
{
    private readonly string _settingsPath;

    public SettingsStore(string settingsPath)
    {
        _settingsPath = settingsPath;
        var dir = global::System.IO.Path.GetDirectoryName(settingsPath);
        if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
    }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_settingsPath)) return new AppSettings();
            var json = File.ReadAllText(_settingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_settingsPath, json);
    }

    public string SettingsPath => _settingsPath;
}
