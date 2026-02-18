using System.Drawing;
using System.Windows.Automation;

namespace BoltMacro;

public sealed class RoiDiffWatcher : IWatcher
{
    private readonly RoiMonitorService _roiMonitor;
    public event Action<Observation>? OnObservation;

    public RoiDiffWatcher(RoiMonitorService roiMonitor) => _roiMonitor = roiMonitor;

    public void Start(RuleModel rule, CancellationToken externalCt)
    {
        _roiMonitor.StartOrReplace(rule, (rid, metric) =>
        {
            double confidence = Math.Clamp(metric, 0, 1);
            OnObservation?.Invoke(new Observation
            {
                RuleId = rid,
                SourceType = ObservationSourceType.RoiDiff,
                Scope = "roi",
                TimestampUtc = DateTime.UtcNow,
                Confidence = confidence,
                DebugReason = $"ROI metric={metric:0.000} threshold={rule.Trigger.Threshold:0.000}",
                Payload = new Dictionary<string, string>
                {
                    ["metric"] = metric.ToString("0.000"),
                    ["threshold"] = rule.Trigger.Threshold.ToString("0.000")
                }
            });
        }, _ => { }, externalCt);
    }

    public void Stop(string ruleId) => _roiMonitor.Stop(ruleId);
    public bool IsRunning(string ruleId) => _roiMonitor.IsRunning(ruleId);
    public void Dispose() { }
}

public sealed class UiaWatcher : IWatcher
{
    private readonly object _lock = new();
    private readonly Dictionary<string, CancellationTokenSource> _watchers = new();
    public event Action<Observation>? OnObservation;

    public void Start(RuleModel rule, CancellationToken externalCt)
    {
        if (!rule.Trigger.UseUiaWatcher) return;
        Stop(rule.Id);

        var cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        lock (_lock) _watchers[rule.Id] = cts;

        var worker = new Thread(() => RunLoop(rule, cts.Token)) { IsBackground = true };
        worker.SetApartmentState(ApartmentState.MTA);
        worker.Start();
    }

    private void RunLoop(RuleModel rule, CancellationToken ct)
    {
        string lastText = string.Empty;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var element = ResolveElement(rule.Trigger.UiaSelector);
                if (element is not null)
                {
                    string text = ReadElementText(element);
                    bool changed = !string.Equals(lastText, text, StringComparison.Ordinal);
                    lastText = text;
                    double confidence = RuleEvaluator.MatchTextConfidence(text, rule.Trigger.UiaExpectedText, rule.Trigger.UiaMatchMode);
                    OnObservation?.Invoke(new Observation
                    {
                        RuleId = rule.Id,
                        SourceType = ObservationSourceType.Uia,
                        Scope = "window",
                        TimestampUtc = DateTime.UtcNow,
                        Confidence = confidence,
                        DebugReason = changed ? "UIA text changed" : "UIA text sampled",
                        Payload = new Dictionary<string, string>
                        {
                            ["text"] = text,
                            ["selector"] = rule.Trigger.UiaSelector ?? "focused"
                        }
                    });
                }
                else
                {
                    OnObservation?.Invoke(new Observation
                    {
                        RuleId = rule.Id,
                        SourceType = ObservationSourceType.Uia,
                        Scope = "window",
                        TimestampUtc = DateTime.UtcNow,
                        Confidence = 0,
                        DebugReason = "UIA element not found",
                        Payload = new Dictionary<string, string>()
                    });
                }
            }
            catch (Exception ex)
            {
                OnObservation?.Invoke(new Observation
                {
                    RuleId = rule.Id,
                    SourceType = ObservationSourceType.Uia,
                    Scope = "window",
                    TimestampUtc = DateTime.UtcNow,
                    Confidence = 0,
                    DebugReason = $"UIA watcher error: {ex.Message}",
                    Payload = new Dictionary<string, string>()
                });
            }

            Thread.Sleep(Math.Max(200, 1000 / Math.Clamp(rule.Trigger.UiaSamplingHz, 1, 10)));
        }
    }

    private static AutomationElement? ResolveElement(string? selector)
    {
        if (string.IsNullOrWhiteSpace(selector))
            return AutomationElement.FocusedElement;

        // format: aid=<..>;name~=<..>;ct=<ControlType>
        var root = AutomationElement.RootElement;
        if (root is null) return null;

        string? aid = null, nameContains = null, controlType = null;
        foreach (var part in selector.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.StartsWith("aid=", StringComparison.OrdinalIgnoreCase)) aid = part[4..];
            if (part.StartsWith("name~=", StringComparison.OrdinalIgnoreCase)) nameContains = part[6..];
            if (part.StartsWith("ct=", StringComparison.OrdinalIgnoreCase)) controlType = part[3..];
        }

        var conditions = new List<Condition>();
        if (!string.IsNullOrWhiteSpace(aid)) conditions.Add(new PropertyCondition(AutomationElement.AutomationIdProperty, aid));
        if (!string.IsNullOrWhiteSpace(controlType))
            conditions.Add(new PropertyCondition(AutomationElement.ControlTypeProperty, ToControlType(controlType)));

        Condition cond = conditions.Count switch
        {
            0 => Condition.TrueCondition,
            1 => conditions[0],
            _ => new AndCondition(conditions.ToArray())
        };

        var candidate = root.FindFirst(TreeScope.Descendants, cond);
        if (candidate is null) return null;
        if (!string.IsNullOrWhiteSpace(nameContains))
        {
            if (!(candidate.Current.Name ?? string.Empty).Contains(nameContains, StringComparison.OrdinalIgnoreCase))
                return null;
        }

        return candidate;
    }

    private static string ReadElementText(AutomationElement element)
    {
        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var p) && p is ValuePattern vp)
            return vp.Current.Value ?? string.Empty;
        return element.Current.Name ?? string.Empty;
    }

    public void Stop(string ruleId)
    {
        CancellationTokenSource? cts = null;
        lock (_lock)
        {
            if (_watchers.TryGetValue(ruleId, out var found))
            {
                cts = found;
                _watchers.Remove(ruleId);
            }
        }
        if (cts is not null)
        {
            try { cts.Cancel(); } catch { }
            cts.Dispose();
        }
    }

    public bool IsRunning(string ruleId)
    {
        lock (_lock) return _watchers.ContainsKey(ruleId);
    }

    public void Dispose()
    {
        List<string> ids;
        lock (_lock) ids = _watchers.Keys.ToList();
        foreach (var id in ids) Stop(id);
    }


    private static ControlType ToControlType(string name) => name.ToLowerInvariant() switch
    {
        "button" => ControlType.Button,
        "edit" => ControlType.Edit,
        "text" => ControlType.Text,
        "window" => ControlType.Window,
        _ => ControlType.Pane
    };
}

public sealed class OcrWatcher : IWatcher
{
    private readonly object _lock = new();
    private readonly Dictionary<string, CancellationTokenSource> _watchers = new();
    public event Action<Observation>? OnObservation;

    public void Start(RuleModel rule, CancellationToken externalCt)
    {
        if (!rule.Trigger.UseOcrWatcher) return;
        Stop(rule.Id);

        var cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        lock (_lock) _watchers[rule.Id] = cts;
        Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    var center = new Point(rule.Trigger.OcrRegionX + (rule.Trigger.OcrRegionW / 2), rule.Trigger.OcrRegionY + (rule.Trigger.OcrRegionH / 2));
                    var el = AutomationElement.FromPoint(new System.Windows.Point(center.X, center.Y));
                    string text = el?.Current.Name ?? string.Empty;
                    double conf = RuleEvaluator.MatchTextConfidence(text, rule.Trigger.OcrExpectedText, rule.Trigger.OcrMatchMode);
                    OnObservation?.Invoke(new Observation
                    {
                        RuleId = rule.Id,
                        SourceType = ObservationSourceType.Ocr,
                        Scope = "roi",
                        TimestampUtc = DateTime.UtcNow,
                        Confidence = conf,
                        DebugReason = string.IsNullOrWhiteSpace(text) ? "OCR fallback sampled empty text" : "OCR fallback text sampled",
                        Payload = new Dictionary<string, string>
                        {
                            ["tokens"] = string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(8)),
                            ["rawStored"] = "false"
                        }
                    });
                }
                catch (Exception ex)
                {
                    OnObservation?.Invoke(new Observation
                    {
                        RuleId = rule.Id,
                        SourceType = ObservationSourceType.Ocr,
                        Scope = "roi",
                        TimestampUtc = DateTime.UtcNow,
                        Confidence = 0,
                        DebugReason = $"OCR watcher degraded: {ex.Message}",
                        Payload = new Dictionary<string, string> { ["rawStored"] = "false" }
                    });
                }

                await Task.Delay(Math.Max(200, rule.Trigger.OcrIntervalMs), cts.Token).ConfigureAwait(false);
            }
        }, cts.Token);
    }

    public void Stop(string ruleId)
    {
        CancellationTokenSource? cts = null;
        lock (_lock)
        {
            if (_watchers.TryGetValue(ruleId, out var found))
            {
                cts = found;
                _watchers.Remove(ruleId);
            }
        }
        if (cts is not null)
        {
            try { cts.Cancel(); } catch { }
            cts.Dispose();
        }
    }

    public bool IsRunning(string ruleId)
    {
        lock (_lock) return _watchers.ContainsKey(ruleId);
    }

    public void Dispose()
    {
        List<string> ids;
        lock (_lock) ids = _watchers.Keys.ToList();
        foreach (var id in ids) Stop(id);
    }
}
