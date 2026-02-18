using System.Collections.Concurrent;
using System.Drawing.Imaging;

namespace BoltMacro;

public sealed class RoiDiffWatcher : IWatcher
{
    private readonly RoiMonitorService _roiMonitor;
    private readonly Action<TimelineEvent> _log;

    public RoiDiffWatcher(RoiMonitorService roiMonitor, Action<TimelineEvent> log)
    {
        _roiMonitor = roiMonitor;
        _log = log;
    }

    public event Action<Observation>? OnObservation;

    public void Start(RuleModel rule, CancellationToken externalCt)
    {
        _roiMonitor.StartOrReplace(rule, (rid, metric) =>
        {
            OnObservation?.Invoke(new Observation
            {
                SourceType = ObservationSourceType.RoiDiff,
                Scope = $"rule:{rid}:roi",
                TimestampUtc = DateTime.UtcNow,
                Confidence = Math.Clamp(metric, 0, 1),
                Payload = new Dictionary<string, string>
                {
                    ["metric"] = metric.ToString("0.000"),
                    ["threshold"] = rule.Trigger.Threshold.ToString("0.000")
                },
                DebugReason = $"ROI diff {metric:0.000} vs threshold {rule.Trigger.Threshold:0.000}"
            });
        }, _log, externalCt);
    }

    public void Stop(string ruleId) => _roiMonitor.Stop(ruleId);
    public bool IsRunning(string ruleId) => _roiMonitor.IsRunning(ruleId);
    public void Dispose() { }
}

public sealed class UiaWatcher : IWatcher
{
    private readonly Action<TimelineEvent> _log;
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _worker;
    private readonly HashSet<string> _activeRuleIds = new();
    private readonly object _lock = new();

    public event Action<Observation>? OnObservation;

    public UiaWatcher(Action<TimelineEvent> log)
    {
        _log = log;
        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "BoltMacro.UIA.MTA"
        };
        _worker.SetApartmentState(ApartmentState.MTA);
        _worker.Start();
    }

    public void Start(RuleModel rule, CancellationToken externalCt)
    {
        _queue.Add(() =>
        {
            lock (_lock) _activeRuleIds.Add(rule.Id);
            _log(new TimelineEvent { Source = TimelineSource.Trigger, Message = $"[SmartRules/UIA] Subscribed ruleId={rule.Id} selector={rule.Smart.UiaSelectorRecipe}" });
            // Stage-1 minimal provider: emit keepalive to prove thread/queue sequencing.
            OnObservation?.Invoke(new Observation
            {
                SourceType = ObservationSourceType.Uia,
                Scope = $"rule:{rule.Id}:uia",
                TimestampUtc = DateTime.UtcNow,
                Confidence = 0.25,
                Payload = new Dictionary<string, string> { ["selector"] = rule.Smart.UiaSelectorRecipe },
                DebugReason = "UIA watcher armed (MTA subscription sequence healthy)."
            });
        });
    }

    public void Stop(string ruleId)
    {
        if (_queue.IsAddingCompleted) return;

        _queue.Add(() =>
        {
            bool removed;
            lock (_lock) removed = _activeRuleIds.Remove(ruleId);
            if (removed)
                _log(new TimelineEvent { Source = TimelineSource.Trigger, Message = $"[SmartRules/UIA] Unsubscribed ruleId={ruleId}" });
        });
    }

    public bool IsRunning(string ruleId)
    {
        lock (_lock) return _activeRuleIds.Contains(ruleId);
    }

    private void WorkerLoop()
    {
        foreach (var action in _queue.GetConsumingEnumerable())
        {
            try { action(); }
            catch (Exception ex)
            {
                _log(new TimelineEvent { Source = TimelineSource.Trigger, Severity = TimelineSeverity.Warn, Message = $"[SmartRules/UIA] fail-soft error: {ex.Message}" });
            }
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        try { _worker.Join(TimeSpan.FromMilliseconds(500)); } catch { }
        _queue.Dispose();
    }
}

public sealed class OcrWatcher : IWatcher
{
    private readonly Action<TimelineEvent> _log;
    private readonly object _lock = new();
    private readonly Dictionary<string, CancellationTokenSource> _watchers = new();

    public event Action<Observation>? OnObservation;

    public OcrWatcher(Action<TimelineEvent> log)
    {
        _log = log;
    }

    public void Start(RuleModel rule, CancellationToken externalCt)
    {
        Stop(rule.Id);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        lock (_lock) _watchers[rule.Id] = cts;

        _ = Task.Run(async () =>
        {
            var intervalMs = Math.Max(100, 1000 / Math.Max(1, rule.Trigger.SamplingHz));
            string? lastToken = null;

            while (!cts.IsCancellationRequested)
            {
                try
                {
                    var roi = rule.Trigger.ContextRoi ?? rule.Trigger.Roi;
                    if (roi is null)
                    {
                        await Task.Delay(intervalMs, cts.Token).ConfigureAwait(false);
                        continue;
                    }

                    using var bmp = new Bitmap(Math.Max(1, roi.W), Math.Max(1, roi.H), PixelFormat.Format24bppRgb);
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.CopyFromScreen(roi.X, roi.Y, 0, 0, bmp.Size, CopyPixelOperation.SourceCopy);
                    }

                    var token = FastTokenFromBitmap(bmp, rule.Smart.OcrCaseInsensitive);
                    var confidence = OcrTextMatcher.MatchConfidence(token, rule.Smart);
                    var changed = !string.Equals(lastToken, token, StringComparison.Ordinal);
                    lastToken = token;

                    OnObservation?.Invoke(new Observation
                    {
                        SourceType = ObservationSourceType.Ocr,
                        Scope = $"rule:{rule.Id}:ocr",
                        TimestampUtc = DateTime.UtcNow,
                        Confidence = confidence,
                        Payload = new Dictionary<string, string>
                        {
                            ["token"] = token,
                            ["changed"] = changed ? "1" : "0"
                        },
                        DebugReason = changed
                            ? $"OCR token changed; pattern confidence={confidence:0.00}"
                            : $"OCR token stable; pattern confidence={confidence:0.00}"
                    });
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _log(new TimelineEvent { Source = TimelineSource.Trigger, Severity = TimelineSeverity.Warn, Message = $"[SmartRules/OCR] fail-soft error: {ex.Message}" });
                }

                try
                {
                    await Task.Delay(intervalMs, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }, cts.Token);
    }

    private static string FastTokenFromBitmap(Bitmap bmp, bool caseInsensitive)
    {
        long lum = 0;
        int edges = 0;
        int samples = 0;
        var stepY = Math.Max(1, bmp.Height / 18);
        var stepX = Math.Max(1, bmp.Width / 18);
        Color? prev = null;

        for (int y = 0; y < bmp.Height; y += stepY)
        {
            for (int x = 0; x < bmp.Width; x += stepX)
            {
                var c = bmp.GetPixel(x, y);
                var l = (c.R * 30 + c.G * 59 + c.B * 11) / 100;
                lum += l;
                samples++;

                if (prev is Color p && Math.Abs(l - ((p.R * 30 + p.G * 59 + p.B * 11) / 100)) > 24)
                    edges++;

                prev = c;
            }
        }

        var avgLum = samples > 0 ? lum / samples : 0;
        var token = $"lum_{avgLum:x}_edge_{edges:x}";
        return caseInsensitive ? token.ToLowerInvariant() : token;
    }

    public void Stop(string ruleId)
    {
        CancellationTokenSource? cts = null;
        lock (_lock)
        {
            if (_watchers.TryGetValue(ruleId, out cts)) _watchers.Remove(ruleId);
        }

        if (cts is null) return;
        cts.Cancel();
        cts.Dispose();
    }

    public bool IsRunning(string ruleId)
    {
        lock (_lock) return _watchers.ContainsKey(ruleId);
    }

    public void Dispose()
    {
        string[] ids;
        lock (_lock) ids = _watchers.Keys.ToArray();
        foreach (var id in ids) Stop(id);
    }
}

public sealed class WatcherCoordinator : IDisposable
{
    private readonly RoiDiffWatcher _roi;
    private readonly UiaWatcher _uia;
    private readonly OcrWatcher _ocr;

    public WatcherCoordinator(RoiDiffWatcher roi, UiaWatcher uia, OcrWatcher ocr)
    {
        _roi = roi;
        _uia = uia;
        _ocr = ocr;

        _roi.OnObservation += ForwardObservation;
        _uia.OnObservation += ForwardObservation;
        _ocr.OnObservation += ForwardObservation;
    }

    public event Action<string, Observation>? OnObservation;

    public void Start(RuleModel rule, CancellationToken ct)
    {
        _roi.Start(rule, ct);
        if (rule.Smart.EnableUiaWatcher) _uia.Start(rule, ct);
        if (rule.Smart.EnableOcrWatcher) _ocr.Start(rule, ct);
    }

    private void ForwardObservation(Observation observation)
    {
        var ruleId = TryParseRuleId(observation.Scope);
        if (string.IsNullOrWhiteSpace(ruleId)) return;
        OnObservation?.Invoke(ruleId, observation);
    }

    private static string? TryParseRuleId(string scope)
    {
        if (string.IsNullOrWhiteSpace(scope)) return null;
        // expected scope: rule:{ruleId}:sensor
        var parts = scope.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 3) return null;
        return string.Equals(parts[0], "rule", StringComparison.OrdinalIgnoreCase) ? parts[1] : null;
    }

    public void Stop(string ruleId)
    {
        _roi.Stop(ruleId);
        _uia.Stop(ruleId);
        _ocr.Stop(ruleId);
    }

    public bool IsRunning(string ruleId) => _roi.IsRunning(ruleId) || _uia.IsRunning(ruleId) || _ocr.IsRunning(ruleId);

    public void Dispose()
    {
        _roi.Dispose();
        _uia.Dispose();
        _ocr.Dispose();
    }
}
