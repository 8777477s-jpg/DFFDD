using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using System.Windows.Forms;

namespace BoltMacro;

public interface IWatcher
{
    event Action<Observation>? OnObservation;
    void Start(RuleModel rule, CancellationToken ct);
    void Stop(string ruleId);
}

public sealed class RoiDiffWatcher : IWatcher
{
    private readonly RoiMonitorService _roi;
    private readonly Action<TimelineEvent> _log;
    private readonly Dictionary<string, CancellationTokenSource> _tokens = new();

    public event Action<Observation>? OnObservation;
    public event Action<string, double>? OnTriggered;

    public RoiDiffWatcher(RoiMonitorService roi, Action<TimelineEvent> log)
    {
        _roi = roi;
        _log = log;
    }

    public void Start(RuleModel rule, CancellationToken ct)
    {
        Stop(rule.Id);
        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _tokens[rule.Id] = linked;

        _roi.StartOrReplace(rule,
            onTriggered: (rid, metric) =>
            {
                OnObservation?.Invoke(new Observation
                {
                    SourceType = ObservationSourceType.ROI_DIFF,
                    Scope = $"rule:{rid}:roi",
                    TimestampUtc = DateTime.UtcNow,
                    Confidence = Math.Clamp(metric, 0, 1),
                    DebugReason = $"ROI diff metric {metric:0.000} exceeded threshold {rule.Trigger.Threshold:0.000}",
                    Payload = new Dictionary<string, string>
                    {
                        ["metric"] = metric.ToString("0.000"),
                        ["threshold"] = rule.Trigger.Threshold.ToString("0.000")
                    }
                });
                OnTriggered?.Invoke(rid, metric);
            },
            log: _log,
            externalCt: linked.Token);
    }

    public void Stop(string ruleId)
    {
        _roi.Stop(ruleId);
        if (_tokens.Remove(ruleId, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
    }
}

public sealed class UiaWatcher : IWatcher, IDisposable
{
    private readonly Action<TimelineEvent> _log;
    private readonly AppSettings _settings;
    private readonly Thread _thread;
    private readonly Channel<Action> _queue = Channel.CreateUnbounded<Action>();
    private readonly Dictionary<string, CancellationTokenSource> _ruleTokens = new();

    public event Action<Observation>? OnObservation;

    public UiaWatcher(Action<TimelineEvent> log, AppSettings settings)
    {
        _log = log;
        _settings = settings;
        _thread = new Thread(ThreadLoop) { IsBackground = true, Name = "BoltMacro.UIA.MTA" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    private void ThreadLoop()
    {
        while (_queue.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
        {
            while (_queue.Reader.TryRead(out var work))
            {
                try { work(); }
                catch (Exception ex)
                {
                    _log(new TimelineEvent { Source = TimelineSource.Trigger, Severity = TimelineSeverity.Warn, Message = $"[SmartRules][UIA] Worker error: {ex.Message}" });
                }
            }
        }
    }

    public void Start(RuleModel rule, CancellationToken ct)
    {
        Stop(rule.Id);
        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ruleTokens[rule.Id] = linked;

        _queue.Writer.TryWrite(() =>
        {
            _log(new TimelineEvent { Source = TimelineSource.Trigger, Message = $"[SmartRules][UIA] Start ruleId={rule.Id} (MTA serialized subscription)" });
            _ = Task.Run(async () =>
            {
                string last = string.Empty;
                while (!linked.IsCancellationRequested)
                {
                    if (!_settings.UiaModuleEnabled || !rule.UiaTrigger.Enabled)
                    {
                        await Task.Delay(400, linked.Token).ConfigureAwait(false);
                        continue;
                    }

                    var hwnd = NativeMethods.GetForegroundWindow();
                    string title = WindowInfo.GetWindowTitle(hwnd);
                    if (!string.Equals(last, title, StringComparison.Ordinal))
                    {
                        last = title;
                        double conf = EvaluateTitle(rule, title);
                        OnObservation?.Invoke(new Observation
                        {
                            SourceType = ObservationSourceType.UIA,
                            Scope = $"rule:{rule.Id}:window:{hwnd}",
                            TimestampUtc = DateTime.UtcNow,
                            Confidence = conf,
                            DebugReason = $"Foreground title observed '{title}'",
                            Payload = new Dictionary<string, string>
                            {
                                ["title"] = title,
                                ["process"] = WindowInfo.GetProcessName(hwnd)
                            }
                        });
                    }

                    await Task.Delay(300, linked.Token).ConfigureAwait(false);
                }
            }, linked.Token);
        });
    }

    private static double EvaluateTitle(RuleModel rule, string title)
    {
        if (!rule.UiaTrigger.Enabled) return 0;
        if (string.IsNullOrWhiteSpace(rule.UiaTrigger.WindowTitleContains)) return 0.40;
        return title.Contains(rule.UiaTrigger.WindowTitleContains, StringComparison.OrdinalIgnoreCase) ? 0.90 : 0.05;
    }

    public void Stop(string ruleId)
    {
        if (_ruleTokens.Remove(ruleId, out var cts))
        {
            _queue.Writer.TryWrite(() => _log(new TimelineEvent { Source = TimelineSource.Trigger, Message = $"[SmartRules][UIA] Stop ruleId={ruleId} (MTA serialized unsubscription)" }));
            cts.Cancel();
            cts.Dispose();
        }
    }

    public void Dispose()
    {
        foreach (var key in _ruleTokens.Keys.ToList()) Stop(key);
        _queue.Writer.Complete();
    }
}

public sealed class OcrWatcher : IWatcher
{
    private readonly Action<TimelineEvent> _log;
    private readonly AppSettings _settings;
    private readonly Dictionary<string, CancellationTokenSource> _tokens = new();

    public event Action<Observation>? OnObservation;

    public OcrWatcher(Action<TimelineEvent> log, AppSettings settings)
    {
        _log = log;
        _settings = settings;
    }

    public void Start(RuleModel rule, CancellationToken ct)
    {
        Stop(rule.Id);
        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _tokens[rule.Id] = linked;

        _ = Task.Run(async () =>
        {
            int hz = Math.Clamp(rule.OcrTrigger.SamplingHz, 1, 10);
            int interval = 1000 / hz;
            while (!linked.IsCancellationRequested)
            {
                string observedText = string.Empty;
                double conf = 0;
                string reason;
                if (!_settings.OcrModuleEnabled || !rule.OcrTrigger.Enabled)
                {
                    reason = "OCR watcher disabled by settings or rule.";
                }
                else
                {
                    // Minimal offline-safe implementation: use active window title as text token source.
                    observedText = WindowInfo.GetWindowTitle(NativeMethods.GetForegroundWindow());
                    conf = EvaluatePattern(rule.OcrTrigger, observedText);
                    reason = $"OCR-text proxy matched with confidence {conf:0.00}";
                }

                OnObservation?.Invoke(new Observation
                {
                    SourceType = ObservationSourceType.OCR,
                    Scope = $"rule:{rule.Id}:ocr",
                    TimestampUtc = DateTime.UtcNow,
                    Confidence = conf,
                    DebugReason = reason,
                    Payload = new Dictionary<string, string>
                    {
                        ["tokens"] = observedText,
                        ["mode"] = rule.OcrTrigger.MatchMode.ToString(),
                        ["pattern"] = rule.OcrTrigger.Pattern ?? string.Empty
                    }
                });

                await Task.Delay(interval, linked.Token).ConfigureAwait(false);
            }
        }, linked.Token);
    }

    private static double EvaluatePattern(OcrTrigger cfg, string text)
    {
        if (string.IsNullOrWhiteSpace(cfg.Pattern)) return 0.35;
        var pattern = cfg.Pattern ?? string.Empty;
        var comp = cfg.CaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        bool hit = cfg.MatchMode switch
        {
            TextMatchMode.Contains => text.Contains(pattern, comp),
            TextMatchMode.Equals => string.Equals(text.Trim(), pattern.Trim(), comp),
            TextMatchMode.Regex => Regex.IsMatch(text, pattern, cfg.CaseInsensitive ? RegexOptions.IgnoreCase : RegexOptions.None),
            _ => false
        };
        return hit ? 0.92 : 0.03;
    }

    public void Stop(string ruleId)
    {
        if (_tokens.Remove(ruleId, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
    }
}

public sealed class RuleEvaluationResult
{
    public double Score { get; set; }
    public string Explanation { get; set; } = string.Empty;
}

public sealed class RuleEvaluator
{
    public RuleEvaluationResult Evaluate(RuleModel rule, IReadOnlyList<Observation> observations)
    {
        double roi = observations.Where(x => x.SourceType == ObservationSourceType.ROI_DIFF).Select(x => x.Confidence).DefaultIfEmpty(0).Max();
        double uia = observations.Where(x => x.SourceType == ObservationSourceType.UIA).Select(x => x.Confidence).DefaultIfEmpty(0).Max();
        double ocr = observations.Where(x => x.SourceType == ObservationSourceType.OCR).Select(x => x.Confidence).DefaultIfEmpty(0).Max();

        var w = rule.Smart;
        var score = Math.Clamp((roi * w.RoiWeight) + (uia * w.UiaWeight) + (ocr * w.OcrWeight), 0, 1);

        var topReasons = observations
            .OrderByDescending(x => x.Confidence)
            .Take(3)
            .Select(x => $"{x.SourceType}:{x.Confidence:0.00} ({x.DebugReason})")
            .ToArray();

        return new RuleEvaluationResult
        {
            Score = score,
            Explanation = topReasons.Length == 0 ? "No observations yet." : string.Join(" | ", topReasons)
        };
    }
}

public sealed class SmartRuleState
{
    public RuleModel Rule { get; init; } = new();
    public List<Observation> Observations { get; } = new();
    public int ConsecutivePasses { get; set; }
    public DateTime CooldownUntilUtc { get; set; }
    public RuleScoreSnapshot LastScore { get; set; } = new();
}

public sealed class SmartRulesOrchestrator : IDisposable
{
    private readonly Storage _storage;
    private readonly TimelineService _timeline;
    private readonly RoiDiffWatcher _roiWatcher;
    private readonly UiaWatcher _uiaWatcher;
    private readonly OcrWatcher _ocrWatcher;
    private readonly RuleEvaluator _evaluator = new();
    private readonly ConcurrentDictionary<string, SmartRuleState> _states = new();
    private readonly Action<string, double, string> _onDecision;
    private readonly AppSettings _settings;

    public SmartRulesOrchestrator(Storage storage, TimelineService timeline, RoiMonitorService roi, AppSettings settings, Action<string, double, string> onDecision)
    {
        _storage = storage;
        _timeline = timeline;
        _onDecision = onDecision;
        _settings = settings;

        _roiWatcher = new RoiDiffWatcher(roi, ev => _timeline.Add(ev));
        _uiaWatcher = new UiaWatcher(ev => _timeline.Add(ev), settings);
        _ocrWatcher = new OcrWatcher(ev => _timeline.Add(ev), settings);

        _roiWatcher.OnObservation += obs => HandleObservation(obs);
        _uiaWatcher.OnObservation += obs => HandleObservation(obs);
        _ocrWatcher.OnObservation += obs => HandleObservation(obs);
        _roiWatcher.OnTriggered += (rid, metric) =>
        {
            if (_states.TryGetValue(rid, out var state))
            {
                EvaluateAndDecide(state, forceBecauseLegacyFire: true, metric);
            }
        };
    }

    public void StartOrReplace(RuleModel rule, CancellationToken ct)
    {
        Stop(rule.Id);
        var state = new SmartRuleState
        {
            Rule = rule,
            LastScore = new RuleScoreSnapshot { RuleId = rule.Id, Score = 0, Explanation = "No observations yet." }
        };
        _states[rule.Id] = state;

        _roiWatcher.Start(rule, ct);
        _uiaWatcher.Start(rule, ct);
        _ocrWatcher.Start(rule, ct);
        _timeline.Add(new TimelineEvent { Source = TimelineSource.Trigger, Message = $"[SmartRules] Monitoring ON ruleId={rule.Id} (ROI/UIA/OCR active where enabled)." });
    }

    public void Stop(string ruleId)
    {
        _roiWatcher.Stop(ruleId);
        _uiaWatcher.Stop(ruleId);
        _ocrWatcher.Stop(ruleId);
        _states.TryRemove(ruleId, out _);
    }

    public void StopAll()
    {
        foreach (var id in _states.Keys.ToArray()) Stop(id);
    }

    public bool IsRunning(string ruleId) => _states.ContainsKey(ruleId);

    public RuleScoreSnapshot GetLastScore(string ruleId)
        => _states.TryGetValue(ruleId, out var state)
            ? state.LastScore
            : new RuleScoreSnapshot { RuleId = ruleId, Score = 0, Explanation = "No score yet." };

    private void HandleObservation(Observation obs)
    {
        string? ruleId = TryGetRuleId(obs.Scope);
        if (ruleId is null) return;
        if (!_states.TryGetValue(ruleId, out var state)) return;

        lock (state)
        {
            state.Observations.Add(obs);
            if (state.Observations.Count > 32)
                state.Observations.RemoveRange(0, state.Observations.Count - 32);

            EvaluateAndDecide(state, forceBecauseLegacyFire: false, metric: obs.Confidence);
        }
    }

    private void EvaluateAndDecide(SmartRuleState state, bool forceBecauseLegacyFire, double metric)
    {
        if (!state.Rule.Smart.Enabled || !_settings.SmartRulesEnabled) return;
        var result = _evaluator.Evaluate(state.Rule, state.Observations);
        state.LastScore = new RuleScoreSnapshot
        {
            RuleId = state.Rule.Id,
            Score = result.Score,
            Explanation = result.Explanation,
            TimestampUtc = DateTime.UtcNow
        };

        _storage.InsertRuleScore(state.LastScore);
        _timeline.Add(new TimelineEvent
        {
            Source = TimelineSource.Trigger,
            Message = $"[SmartRules] score ruleId={state.Rule.Id} score={result.Score:0.000} why={Trim(result.Explanation)}"
        });

        if (DateTime.UtcNow < state.CooldownUntilUtc) return;

        bool passes = result.Score >= state.Rule.Smart.FireThreshold;
        state.ConsecutivePasses = passes ? state.ConsecutivePasses + 1 : 0;

        if (!forceBecauseLegacyFire && state.ConsecutivePasses < Math.Max(1, state.Rule.Smart.ConsecutiveEventsRequired))
            return;

        state.ConsecutivePasses = 0;
        state.CooldownUntilUtc = DateTime.UtcNow.AddMilliseconds(Math.Max(0, state.Rule.Smart.CooldownMs));
        _onDecision(state.Rule.Id, metric, result.Explanation);
    }

    private static string? TryGetRuleId(string scope)
    {
        if (string.IsNullOrWhiteSpace(scope)) return null;
        if (!scope.StartsWith("rule:")) return null;
        var span = scope.AsSpan(5);
        int idx = span.IndexOf(':');
        if (idx < 0) return span.ToString();
        return span[..idx].ToString();
    }

    private static string Trim(string value)
        => value.Length <= 220 ? value : value[..220] + "…";

    public void Dispose()
    {
        StopAll();
        _uiaWatcher.Dispose();
    }
}

internal static class WindowInfo
{
    public static string GetWindowTitle(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return string.Empty;
        var sb = new System.Text.StringBuilder(256);
        _ = NativeMethods.GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static string GetProcessName(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return string.Empty;
        _ = NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        try
        {
            return System.Diagnostics.Process.GetProcessById((int)pid).ProcessName;
        }
        catch
        {
            return string.Empty;
        }
    }
}
