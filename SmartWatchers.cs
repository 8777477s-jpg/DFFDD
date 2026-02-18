using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Text;

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
    private readonly object _lock = new();
    private readonly Dictionary<string, CancellationTokenSource> _runningRules = new();

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
        Stop(rule.Id);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);

        _queue.Add(() =>
        {
            lock (_lock) _runningRules[rule.Id] = cts;
            _log(new TimelineEvent { Source = TimelineSource.Trigger, Message = $"[SmartRules/UIA] Subscribed ruleId={rule.Id} selector={rule.Smart.UiaSelectorRecipe}" });

            _ = Task.Run(async () =>
            {
                int intervalMs = Math.Max(150, 1000 / Math.Max(1, rule.Trigger.SamplingHz));
                while (!cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        var ctx = GetForegroundWindowContext();
                        var score = EvaluateSelector(ctx, rule.Smart.UiaSelectorRecipe);

                        OnObservation?.Invoke(new Observation
                        {
                            SourceType = ObservationSourceType.Uia,
                            Scope = $"rule:{rule.Id}:uia",
                            TimestampUtc = DateTime.UtcNow,
                            Confidence = score,
                            Payload = new Dictionary<string, string>
                            {
                                ["title"] = ctx.Title,
                                ["class"] = ctx.ClassName,
                                ["process"] = ctx.ProcessName
                            },
                            DebugReason = $"UIA/window context match={score:0.00} ({ctx.ProcessName}/{ctx.ClassName})"
                        });
                    }
                    catch (Exception ex)
                    {
                        _log(new TimelineEvent { Source = TimelineSource.Trigger, Severity = TimelineSeverity.Warn, Message = $"[SmartRules/UIA] fail-soft error: {ex.Message}" });
                    }

                    await Task.Delay(intervalMs, cts.Token).ConfigureAwait(false);
                }
            }, cts.Token);
        });
    }

    public void Stop(string ruleId)
    {
        _queue.Add(() =>
        {
            CancellationTokenSource? cts = null;
            lock (_lock)
            {
                if (_runningRules.TryGetValue(ruleId, out cts))
                    _runningRules.Remove(ruleId);
            }

            if (cts is not null)
            {
                cts.Cancel();
                cts.Dispose();
                _log(new TimelineEvent { Source = TimelineSource.Trigger, Message = $"[SmartRules/UIA] Unsubscribed ruleId={ruleId}" });
            }
        });
    }

    public bool IsRunning(string ruleId)
    {
        lock (_lock) return _runningRules.ContainsKey(ruleId);
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

    private static WindowContext GetForegroundWindowContext()
    {
        IntPtr hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return new WindowContext();

        var titleSb = new StringBuilder(260);
        NativeMethods.GetWindowText(hwnd, titleSb, titleSb.Capacity);

        var clsSb = new StringBuilder(260);
        NativeMethods.GetClassName(hwnd, clsSb, clsSb.Capacity);

        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        string process = string.Empty;
        try
        {
            if (pid != 0) process = Process.GetProcessById((int)pid).ProcessName;
        }
        catch { }

        return new WindowContext
        {
            Title = titleSb.ToString(),
            ClassName = clsSb.ToString(),
            ProcessName = process
        };
    }

    private static double EvaluateSelector(WindowContext ctx, string recipe)
    {
        if (string.IsNullOrWhiteSpace(recipe)) return 0.30;

        var parts = recipe.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return 0.30;

        int hits = 0;
        foreach (var p in parts)
        {
            var kv = p.Split('=', 2, StringSplitOptions.TrimEntries);
            if (kv.Length != 2) continue;

            var key = kv[0].ToLowerInvariant();
            var value = kv[1];
            if (string.IsNullOrWhiteSpace(value)) continue;

            bool ok = key switch
            {
                "process" or "processname" => ctx.ProcessName.Contains(value, StringComparison.OrdinalIgnoreCase),
                "class" or "classname" => ctx.ClassName.Contains(value, StringComparison.OrdinalIgnoreCase),
                "title" or "name" => ctx.Title.Contains(value, StringComparison.OrdinalIgnoreCase),
                _ => false
            };
            if (ok) hits++;
        }

        return Math.Clamp(hits / (double)Math.Max(1, parts.Length), 0, 1);
    }

    private sealed class WindowContext
    {
        public string Title { get; init; } = string.Empty;
        public string ClassName { get; init; } = string.Empty;
        public string ProcessName { get; init; } = string.Empty;
    }

    public void Dispose()
    {
        string[] ids;
        lock (_lock) ids = _runningRules.Keys.ToArray();
        foreach (var id in ids) Stop(id);

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
            var intervalMs = Math.Max(180, 1000 / Math.Max(1, rule.Trigger.SamplingHz));
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

                    var token = FastTokenFromBitmap(bmp);
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
                catch (Exception ex)
                {
                    _log(new TimelineEvent { Source = TimelineSource.Trigger, Severity = TimelineSeverity.Warn, Message = $"[SmartRules/OCR] fail-soft error: {ex.Message}" });
                }

                await Task.Delay(intervalMs, cts.Token).ConfigureAwait(false);
            }
        }, cts.Token);
    }

    private static string FastTokenFromBitmap(Bitmap bmp)
    {
        long luma = 0;
        long contrast = 0;
        int samples = 0;
        int stepY = Math.Max(1, bmp.Height / 24);
        int stepX = Math.Max(1, bmp.Width / 24);

        byte prev = 0;
        bool hasPrev = false;
        for (int y = 0; y < bmp.Height; y += stepY)
        {
            for (int x = 0; x < bmp.Width; x += stepX)
            {
                var c = bmp.GetPixel(x, y);
                byte lum = (byte)((c.R * 30 + c.G * 59 + c.B * 11) / 100);
                luma += lum;
                if (hasPrev) contrast += Math.Abs(lum - prev);
                prev = lum;
                hasPrev = true;
                samples++;
            }
        }

        var avgLum = samples > 0 ? luma / samples : 0;
        var avgCtr = samples > 1 ? contrast / (samples - 1) : 0;
        return $"lum_{avgLum:x2}_ctr_{avgCtr:x2}";
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

    public void Stop(string ruleId)
    {
        _roi.Stop(ruleId);
        _uia.Stop(ruleId);
        _ocr.Stop(ruleId);
    }

    public bool IsRunning(string ruleId) => _roi.IsRunning(ruleId) || _uia.IsRunning(ruleId) || _ocr.IsRunning(ruleId);

    private void ForwardObservation(Observation observation)
    {
        var rid = RuleIdFromScope(observation.Scope);
        if (!string.IsNullOrWhiteSpace(rid))
            OnObservation?.Invoke(rid, observation);
    }

    private static string RuleIdFromScope(string scope)
    {
        // scope: rule:{id}:sensor
        var parts = scope.Split(':', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && parts[0] == "rule" ? parts[1] : string.Empty;
    }

    public void Dispose()
    {
        _roi.Dispose();
        _uia.Dispose();
        _ocr.Dispose();
    }
}
