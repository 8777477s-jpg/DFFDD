using System.Text.Json;
using System.Text.Json.Serialization;

namespace BoltMacro;

public enum AppMode
{
    Idle = 0,
    Recording = 1,
    Playing = 2,
    Monitoring = 3,
    RunningRule = 4
}

public enum RuleState
{
    Disarmed = 0,
    ArmedMonitoring = 1,
    Running = 2
}

public enum RepeatMode
{
    Once = 0,
    RepeatN = 1,
    Infinite = 2
}

public enum TimelineSource
{
    System = 0,
    Recording = 1,
    Playback = 2,
    Trigger = 3,
    Rule = 4,
    Storage = 5
}

public enum TimelineSeverity
{
    Info = 0,
    Warn = 1,
    Error = 2
}

public enum RunStatus
{
    Success = 0,
    Failed = 1,
    Aborted = 2
}

public enum CoordSpace
{
    Screen = 0
}

public enum MouseButton
{
    Left = 0,
    Right = 1,
    Middle = 2
}

public enum TrackingScope
{
    AllMonitors = 0,
    PrimaryMonitor = 1
}

public enum WindowMatchMode
{
    ProcessOnly = 0,
    ProcessAndTitle = 1,
    ProcessAndClass = 2,
    StrictAll = 3
}

public sealed class TimelineEvent
{
    public string Id { get; init; } = IdUtil.NewId();
    public DateTime UtcTime { get; init; } = DateTime.UtcNow;
    public TimelineSource Source { get; init; } = TimelineSource.System;
    public TimelineSeverity Severity { get; init; } = TimelineSeverity.Info;
    public string Message { get; init; } = "";
    public Dictionary<string, string>? Details { get; init; }
}

public sealed class RoiRect
{
    public string MonitorId { get; set; } = "Virtual";
    public int X { get; set; }
    public int Y { get; set; }
    public int W { get; set; }
    public int H { get; set; }
}

public sealed class RelativeRoiRect
{
    public int OffsetX { get; set; }
    public int OffsetY { get; set; }
    public int W { get; set; }
    public int H { get; set; }

    public double? OffsetXFrac { get; set; }
    public double? OffsetYFrac { get; set; }
    public double? WFrac { get; set; }
    public double? HFrac { get; set; }

    public int SourceContextW { get; set; }
    public int SourceContextH { get; set; }
}

public sealed class RoiTrigger
{
    // Legacy field kept for compatibility and fallback path.
    public RoiRect? Roi { get; set; }

    // New context+button model.
    public RoiRect? ContextRoi { get; set; }
    public RelativeRoiRect? ButtonRoiRelative { get; set; }

    public int SamplingHz { get; set; } = 4;
    public double Threshold { get; set; } = 0.12;
    public int DebounceMs { get; set; } = 250;
    public int ConsecutiveHitsRequired { get; set; } = 2;
    public int CooldownMs { get; set; } = 1000;

    // Advanced deterministic visual options.
    public bool UseMultiScaleMatching { get; set; } = true;
    public bool UseOcrAnchors { get; set; } = false;
    public bool UseOrbFallback { get; set; } = false;
    public bool UseWindowFilter { get; set; } = false;
    public string? WindowProcessName { get; set; }
    public string? WindowTitleContains { get; set; }
    public string? WindowClassName { get; set; }
    public WindowMatchMode WindowMatchMode { get; set; } = WindowMatchMode.ProcessOnly;
    public bool AllowGlobalSearchFallback { get; set; } = false;
    public TrackingScope Scope { get; set; } = TrackingScope.AllMonitors;
    public List<double> MatchScales { get; set; } = new() { 0.9, 0.95, 1.0, 1.05, 1.1 };
    public int LocalSearchPaddingPx { get; set; } = 180;
    public int GlobalReacquireEveryNSeconds { get; set; } = 15;
    public double ContextMatchMinScore { get; set; } = 0.72;
    public int ContextJumpMaxPixels { get; set; } = 120;
    public double ContextJumpMinIou { get; set; } = 0.40;
    public int StableTicksRequired { get; set; } = 2;
    public double MetricVarianceMax { get; set; } = 0.20;
    public bool TriggerDebugDetails { get; set; } = false;
    public bool CollectIncidentOnFire { get; set; } = false;

    // Smart rules stage-1 options.
    public bool UseUiaWatcher { get; set; } = false;
    public bool UseOcrWatcher { get; set; } = false;
    public string? UiaSelector { get; set; }
    public string? UiaExpectedText { get; set; }
    public string UiaMatchMode { get; set; } = "contains";
    public int UiaSamplingHz { get; set; } = 4;

    public int OcrRegionX { get; set; } = 0;
    public int OcrRegionY { get; set; } = 0;
    public int OcrRegionW { get; set; } = 200;
    public int OcrRegionH { get; set; } = 60;
    public string? OcrExpectedText { get; set; }
    public string OcrMatchMode { get; set; } = "contains";
    public int OcrIntervalMs { get; set; } = 500;

    public double FireThreshold { get; set; } = 0.65;
    public int ScoreConsecutiveRequired { get; set; } = 1;
    public double RoiWeight { get; set; } = 1.0;
    public double UiaWeight { get; set; } = 0.35;
    public double OcrWeight { get; set; } = 0.35;
}

public sealed class RepeatPolicy
{
    public RepeatMode Mode { get; set; } = RepeatMode.Infinite;
    public int N { get; set; } = 1;
}

public sealed class RuleModel
{
    public string Id { get; set; } = IdUtil.NewId();
    public string Name { get; set; } = "New Rule";
    public bool Enabled { get; set; } = false;
    public RuleState State { get; set; } = RuleState.Disarmed;
    public int RunsDone { get; set; } = 0;

    public string? MacroId { get; set; }
    public RoiTrigger Trigger { get; set; } = new RoiTrigger();
    public RepeatPolicy Repeat { get; set; } = new RepeatPolicy();
}

public sealed class MacroModel
{
    public string Id { get; set; } = IdUtil.NewId();
    public string Name { get; set; } = "Macro";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public List<MacroStep> Steps { get; set; } = new();
}

public sealed class RunRecord
{
    public string Id { get; set; } = IdUtil.NewId();
    public string? RuleId { get; set; }
    public string? MacroId { get; set; }
    public DateTime StartUtc { get; set; }
    public DateTime? EndUtc { get; set; }
    public RunStatus Status { get; set; } = RunStatus.Success;
    public string? ErrorText { get; set; }
}

public record RuleStatePersist
{
    public RuleState State { get; init; } = RuleState.Disarmed;
    public int RunsDone { get; init; } = 0;
}

public static class JsonUtil
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters =
        {
            new JsonStringEnumConverter(),
            new MacroStepJsonConverter()
        }
    };

    public static string ToJson<T>(T obj) => JsonSerializer.Serialize(obj, Options);
    public static T? FromJson<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}

// Steps
public abstract class MacroStep
{
    public int DelayMsBefore { get; set; } = 0;
}

public sealed class DelayStep : MacroStep
{
    public int Ms { get; set; }
}

public sealed class KeyStep : MacroStep
{
    public int VirtualKey { get; set; }
    public bool IsKeyDown { get; set; }
}

public sealed class MouseMoveStep : MacroStep
{
    public int X { get; set; }
    public int Y { get; set; }
    public CoordSpace Space { get; set; } = CoordSpace.Screen;
}

public sealed class MouseClickStep : MacroStep
{
    public int X { get; set; }
    public int Y { get; set; }
    public CoordSpace Space { get; set; } = CoordSpace.Screen;
    public MouseButton Button { get; set; } = MouseButton.Left;
    public int Clicks { get; set; } = 1;
}
