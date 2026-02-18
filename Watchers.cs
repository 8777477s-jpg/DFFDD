using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;

namespace BoltMacro;

public sealed class RoiDiffWatcher : IWatcher
{
    public event Action<string, double>? OnTriggered;
    private readonly RoiMonitorService _roi;
    private readonly TimelineService _timeline;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _tokens = new();
    public event Action<Observation>? OnObservation;

    public RoiDiffWatcher(RoiMonitorService roi, TimelineService timeline)
    {
        _roi = roi;
        _timeline = timeline;
    }

    public void Start(RuleModel rule, CancellationToken externalToken)
    {
        Stop(rule.Id);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        _tokens[rule.Id] = cts;

        _roi.StartOrReplace(
            rule,
            onTriggered: (rid, metric) =>
            {
                OnTriggered?.Invoke(rid, metric);
                OnObservation?.Invoke(new Observation
                {
                    RuleId = rid,
                    SourceType = ObservationSourceType.RoiDiff,
                    Scope = "roi",
                    Confidence = Math.Clamp(metric, 0.0, 1.0),
                    DebugReason = $"ROI metric={metric:0.000} threshold={rule.Trigger.Threshold:0.000}",
                    Payload = new Dictionary<string, string>
                    {
                        ["metric"] = metric.ToString("0.000"),
                        ["threshold"] = rule.Trigger.Threshold.ToString("0.000")
                    }
                });
            },
            log: ev => _timeline.Add(ev),
            externalCt: cts.Token,
            onMetric: (rid, metric, why) =>
            {
                OnObservation?.Invoke(new Observation
                {
                    RuleId = rid,
                    SourceType = ObservationSourceType.RoiDiff,
                    Scope = "roi",
                    Confidence = Math.Clamp(metric, 0.0, 1.0),
                    DebugReason = why,
                    Payload = new Dictionary<string, string> { ["metric"] = metric.ToString("0.000") }
                });
            });
    }

    public void Stop(string ruleId)
    {
        _roi.Stop(ruleId);
        if (_tokens.TryRemove(ruleId, out var cts))
        {
            try { cts.Cancel(); } catch { }
            cts.Dispose();
        }
    }

    public bool IsRunning(string ruleId) => _roi.IsRunning(ruleId);
    public void Dispose() { foreach (var id in _tokens.Keys) Stop(id); }
}

public sealed class UiaWatcher : IWatcher
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Dictionary<string, CancellationTokenSource> _ruleTokens = new();
    private Thread? _thread;
    public event Action<Observation>? OnObservation;

    public UiaWatcher()
    {
        _thread = new Thread(() =>
        {
            while (!_queue.IsCompleted)
            {
                if (_queue.TryTake(out var work, Timeout.Infinite))
                {
                    try { work(); } catch { }
                }
            }
        });
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.IsBackground = true;
        _thread.Start();
    }

    public void Start(RuleModel rule, CancellationToken externalToken)
    {
        Stop(rule.Id);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        _ruleTokens[rule.Id] = cts;
        _queue.Add(() => RunLoop(rule, cts.Token));
    }

    private void RunLoop(RuleModel rule, CancellationToken ct)
    {
        string last = "";
        while (!ct.IsCancellationRequested)
        {
            var hwnd = NativeMethods.GetForegroundWindow();
            string title = GetWindowText(hwnd);
            string proc = NativeMethods.GetProcessNameByWindow(hwnd) ?? "";

            bool hit = false;
            if (!string.IsNullOrWhiteSpace(rule.Smart.UiaSelector.PartialName))
            {
                hit = title.Contains(rule.Smart.UiaSelector.PartialName, StringComparison.OrdinalIgnoreCase);
            }

            string current = $"{proc}|{title}|{hit}";
            if (current != last)
            {
                last = current;
                OnObservation?.Invoke(new Observation
                {
                    RuleId = rule.Id,
                    SourceType = ObservationSourceType.Uia,
                    Scope = "window",
                    Confidence = hit ? 0.85 : 0.05,
                    DebugReason = hit ? $"Foreground title matched '{rule.Smart.UiaSelector.PartialName}'" : "Foreground title did not match selector",
                    Payload = new Dictionary<string, string>
                    {
                        ["process"] = proc,
                        ["title"] = title,
                        ["selector"] = rule.Smart.UiaSelector.PartialName ?? ""
                    }
                });
            }

            Thread.Sleep(Math.Max(100, 1000 / Math.Clamp(rule.Trigger.SamplingHz, 1, 30)));
        }
    }

    public void Stop(string ruleId)
    {
        if (_ruleTokens.TryGetValue(ruleId, out var cts))
        {
            _ruleTokens.Remove(ruleId);
            try { cts.Cancel(); } catch { }
            cts.Dispose();
        }
    }

    public bool IsRunning(string ruleId) => _ruleTokens.ContainsKey(ruleId);

    private static string GetWindowText(IntPtr hwnd)
    {
        var sb = new StringBuilder(512);
        _ = NativeMethods.GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public void Dispose()
    {
        foreach (var id in _ruleTokens.Keys.ToList()) Stop(id);
        _queue.CompleteAdding();
    }
}

public sealed class OcrWatcher : IWatcher
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _tokens = new();
    private readonly AppSettings _settings;
    public event Action<Observation>? OnObservation;

    public OcrWatcher(AppSettings settings)
    {
        _settings = settings;
    }

    public void Start(RuleModel rule, CancellationToken externalToken)
    {
        Stop(rule.Id);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        _tokens[rule.Id] = cts;

        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                if (!_settings.OcrModuleEnabled)
                {
                    OnObservation?.Invoke(new Observation
                    {
                        RuleId = rule.Id,
                        SourceType = ObservationSourceType.Ocr,
                        Scope = "roi",
                        Confidence = 0.0,
                        DebugReason = "OCR disabled in settings (privacy default)",
                        Payload = new Dictionary<string, string> { ["mode"] = "disabled" }
                    });
                    await Task.Delay(1000, cts.Token).ConfigureAwait(false);
                    continue;
                }

                // Minimal pragmatic stage-1 fallback: no raw screenshots persisted, only compact text token from active window title.
                var hwnd = NativeMethods.GetForegroundWindow();
                var title = new StringBuilder(512);
                _ = NativeMethods.GetWindowText(hwnd, title, title.Capacity);
                string token = title.ToString().Trim();
                bool matched = !string.IsNullOrWhiteSpace(rule.Smart.OcrContains)
                    && token.Contains(rule.Smart.OcrContains, StringComparison.OrdinalIgnoreCase);

                OnObservation?.Invoke(new Observation
                {
                    RuleId = rule.Id,
                    SourceType = ObservationSourceType.Ocr,
                    Scope = "window",
                    Confidence = matched ? 0.65 : 0.05,
                    DebugReason = matched ? $"OCR token matched '{rule.Smart.OcrContains}'" : "OCR token did not match",
                    Payload = new Dictionary<string, string>
                    {
                        ["token"] = token,
                        ["contains"] = rule.Smart.OcrContains ?? ""
                    }
                });

                await Task.Delay(Math.Max(200, 1000 / Math.Clamp(rule.Trigger.SamplingHz, 1, 30)), cts.Token).ConfigureAwait(false);
            }
        }, cts.Token);
    }

    public void Stop(string ruleId)
    {
        if (_tokens.TryRemove(ruleId, out var cts))
        {
            try { cts.Cancel(); } catch { }
            cts.Dispose();
        }
    }

    public bool IsRunning(string ruleId) => _tokens.ContainsKey(ruleId);
    public void Dispose() { foreach (var id in _tokens.Keys) Stop(id); }
}
