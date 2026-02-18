using System.Collections.Concurrent;
using System.Drawing.Imaging;
using System.Windows.Automation;

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
    private readonly Dictionary<string, UiaRuleContext> _contexts = new();

    private sealed class UiaRuleContext
    {
        public CancellationTokenSource Cts { get; init; } = new();
        public string SelectorRecipe { get; init; } = "";
        public UiaSelector Selector { get; init; } = new();
        public UiaSnapshot? LastSnapshot { get; set; }
        public DateTime LastEmitUtc { get; set; } = DateTime.MinValue;
    }

    private sealed class UiaSnapshot
    {
        public bool Exists { get; init; }
        public string Name { get; init; } = "";
        public string Value { get; init; } = "";
        public bool IsEnabled { get; init; }
        public string ControlType { get; init; } = "";
        public Rect Rect { get; init; }

        public bool EqualsTo(UiaSnapshot other)
        {
            return Exists == other.Exists &&
                   string.Equals(Name, other.Name, StringComparison.Ordinal) &&
                   string.Equals(Value, other.Value, StringComparison.Ordinal) &&
                   IsEnabled == other.IsEnabled &&
                   string.Equals(ControlType, other.ControlType, StringComparison.Ordinal) &&
                   Math.Abs(Rect.Left - other.Rect.Left) < 1 &&
                   Math.Abs(Rect.Top - other.Rect.Top) < 1 &&
                   Math.Abs(Rect.Width - other.Rect.Width) < 1 &&
                   Math.Abs(Rect.Height - other.Rect.Height) < 1;
        }
    }

    private sealed class UiaSelector
    {
        public string? AutomationId { get; set; }
        public string? ControlType { get; set; }
        public string? NameContains { get; set; }
        public string? ClassName { get; set; }
        public string? ProcessName { get; set; }
        public string? ParentNameContains { get; set; }

        public static UiaSelector Parse(string recipe)
        {
            var sel = new UiaSelector();
            if (string.IsNullOrWhiteSpace(recipe)) return sel;

            var chunks = recipe.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var chunk in chunks)
            {
                var parts = chunk.Split('=', 2, StringSplitOptions.TrimEntries);
                if (parts.Length != 2) continue;

                var key = parts[0].Trim().ToLowerInvariant();
                var value = parts[1].Trim();
                if (string.IsNullOrWhiteSpace(value)) continue;

                switch (key)
                {
                    case "automationid": sel.AutomationId = value; break;
                    case "controltype": sel.ControlType = value; break;
                    case "name":
                    case "namecontains": sel.NameContains = value; break;
                    case "classname": sel.ClassName = value; break;
                    case "process":
                    case "processname": sel.ProcessName = value; break;
                    case "parent":
                    case "parentname": sel.ParentNameContains = value; break;
                }
            }

            return sel;
        }
    }

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
        _queue.Add(() => StartOnWorker(rule, externalCt));
    }

    private void StartOnWorker(RuleModel rule, CancellationToken externalCt)
    {
        StopOnWorker(rule.Id);

        var linked = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        var ctx = new UiaRuleContext
        {
            Cts = linked,
            SelectorRecipe = rule.Smart.UiaSelectorRecipe,
            Selector = UiaSelector.Parse(rule.Smart.UiaSelectorRecipe)
        };
        _contexts[rule.Id] = ctx;

        _log(new TimelineEvent
        {
            Source = TimelineSource.Trigger,
            Message = $"[SmartRules/UIA] Subscribed ruleId={rule.Id} selector={rule.Smart.UiaSelectorRecipe}"
        });

        _ = Task.Run(async () =>
        {
            var intervalMs = Math.Max(120, 1000 / Math.Max(1, rule.Trigger.SamplingHz));
            while (!linked.IsCancellationRequested)
            {
                try
                {
                    UiaSnapshot snapshot;
                    lock (_contexts)
                    {
                        if (!_contexts.TryGetValue(rule.Id, out var currentCtx)) break;
                        snapshot = CaptureSnapshot(currentCtx.Selector);

                        bool changed = currentCtx.LastSnapshot is null || !currentCtx.LastSnapshot.EqualsTo(snapshot);
                        bool dueHeartbeat = (DateTime.UtcNow - currentCtx.LastEmitUtc).TotalSeconds >= 2.5;
                        if (changed || dueHeartbeat)
                        {
                            currentCtx.LastSnapshot = snapshot;
                            currentCtx.LastEmitUtc = DateTime.UtcNow;
                            EmitObservation(rule.Id, snapshot, changed, currentCtx.SelectorRecipe);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log(new TimelineEvent
                    {
                        Source = TimelineSource.Trigger,
                        Severity = TimelineSeverity.Warn,
                        Message = $"[SmartRules/UIA] fail-soft error ruleId={rule.Id}: {ex.Message}"
                    });
                }

                await Task.Delay(intervalMs, linked.Token).ConfigureAwait(false);
            }
        }, linked.Token);
    }

    private void EmitObservation(string ruleId, UiaSnapshot snapshot, bool changed, string selectorRecipe)
    {
        var reason = !snapshot.Exists
            ? "UIA target missing"
            : changed
                ? $"UIA change name='{snapshot.Name}' enabled={snapshot.IsEnabled}"
                : "UIA stable heartbeat";

        var confidence = snapshot.Exists
            ? (snapshot.IsEnabled ? 0.86 : 0.58)
            : 0.08;

        OnObservation?.Invoke(new Observation
        {
            SourceType = ObservationSourceType.Uia,
            Scope = $"rule:{ruleId}:uia",
            TimestampUtc = DateTime.UtcNow,
            Confidence = confidence,
            Payload = new Dictionary<string, string>
            {
                ["exists"] = snapshot.Exists ? "1" : "0",
                ["name"] = snapshot.Name,
                ["value"] = snapshot.Value,
                ["enabled"] = snapshot.IsEnabled ? "1" : "0",
                ["controlType"] = snapshot.ControlType,
                ["selector"] = selectorRecipe
            },
            DebugReason = reason
        });
    }

    private static UiaSnapshot CaptureSnapshot(UiaSelector selector)
    {
        var root = AutomationElement.RootElement;
        var target = FindBySelector(root, selector);
        if (target is null)
        {
            return new UiaSnapshot { Exists = false };
        }

        string value = "";
        try
        {
            if (target.TryGetCurrentPattern(ValuePattern.Pattern, out var p) && p is ValuePattern vp)
                value = vp.Current.Value ?? "";
        }
        catch
        {
        }

        return new UiaSnapshot
        {
            Exists = true,
            Name = target.Current.Name ?? "",
            Value = value,
            IsEnabled = target.Current.IsEnabled,
            ControlType = target.Current.ControlType?.ProgrammaticName ?? "",
            Rect = target.Current.BoundingRectangle
        };
    }

    private static AutomationElement? FindBySelector(AutomationElement root, UiaSelector selector)
    {
        Condition condition = Condition.TrueCondition;
        var parts = new List<Condition>();

        if (!string.IsNullOrWhiteSpace(selector.AutomationId))
            parts.Add(new PropertyCondition(AutomationElement.AutomationIdProperty, selector.AutomationId));

        if (!string.IsNullOrWhiteSpace(selector.ClassName))
            parts.Add(new PropertyCondition(AutomationElement.ClassNameProperty, selector.ClassName));

        if (!string.IsNullOrWhiteSpace(selector.ControlType) && TryMapControlType(selector.ControlType!, out var ct))
            parts.Add(new PropertyCondition(AutomationElement.ControlTypeProperty, ct));

        if (parts.Count == 1) condition = parts[0];
        else if (parts.Count > 1) condition = new AndCondition(parts.ToArray());

        var candidates = root.FindAll(TreeScope.Subtree, condition);
        for (int i = 0; i < candidates.Count; i++)
        {
            var el = candidates[i];
            if (!NameMatches(el, selector.NameContains)) continue;
            if (!ParentMatches(el, selector.ParentNameContains)) continue;
            if (!ProcessMatches(el, selector.ProcessName)) continue;
            return el;
        }

        return null;
    }

    private static bool NameMatches(AutomationElement el, string? part)
    {
        if (string.IsNullOrWhiteSpace(part)) return true;
        return (el.Current.Name ?? "").Contains(part, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ParentMatches(AutomationElement el, string? parentNameContains)
    {
        if (string.IsNullOrWhiteSpace(parentNameContains)) return true;
        var walker = TreeWalker.ControlViewWalker;
        var parent = walker.GetParent(el);
        return parent is not null && (parent.Current.Name ?? "").Contains(parentNameContains, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ProcessMatches(AutomationElement el, string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return true;
        try
        {
            var pid = el.Current.ProcessId;
            var p = System.Diagnostics.Process.GetProcessById(pid);
            return p.ProcessName.Contains(processName, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryMapControlType(string raw, out ControlType controlType)
    {
        var key = raw.Trim().ToLowerInvariant();
        controlType = key switch
        {
            "button" => ControlType.Button,
            "edit" or "textbox" => ControlType.Edit,
            "text" => ControlType.Text,
            "pane" => ControlType.Pane,
            "window" => ControlType.Window,
            "list" => ControlType.List,
            "listitem" => ControlType.ListItem,
            "checkbox" => ControlType.CheckBox,
            "combobox" => ControlType.ComboBox,
            _ => ControlType.Custom
        };
        return controlType != ControlType.Custom;
    }

    public void Stop(string ruleId)
    {
        _queue.Add(() => StopOnWorker(ruleId));
    }

    private void StopOnWorker(string ruleId)
    {
        lock (_contexts)
        {
            if (!_contexts.TryGetValue(ruleId, out var ctx)) return;
            _contexts.Remove(ruleId);
            ctx.Cts.Cancel();
            ctx.Cts.Dispose();
        }

        _log(new TimelineEvent { Source = TimelineSource.Trigger, Message = $"[SmartRules/UIA] Unsubscribed ruleId={ruleId}" });
    }

    public bool IsRunning(string ruleId)
    {
        lock (_contexts) return _contexts.ContainsKey(ruleId);
    }

    private void WorkerLoop()
    {
        foreach (var action in _queue.GetConsumingEnumerable())
        {
            try { action(); }
            catch (Exception ex)
            {
                _log(new TimelineEvent { Source = TimelineSource.Trigger, Severity = TimelineSeverity.Warn, Message = $"[SmartRules/UIA] fail-soft worker error: {ex.Message}" });
            }
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        try { _worker.Join(TimeSpan.FromMilliseconds(500)); } catch { }

        string[] ids;
        lock (_contexts) ids = _contexts.Keys.ToArray();
        foreach (var id in ids) StopOnWorker(id);

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
            var intervalMs = Math.Max(120, 1000 / Math.Max(1, rule.Trigger.SamplingHz));
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

                    var token = BuildCompactVisualToken(bmp);
                    var changed = !string.Equals(lastToken, token, StringComparison.Ordinal);
                    var confidence = OcrTextMatcher.MatchConfidence(token, rule.Smart);
                    var reason = changed
                        ? $"OCR-like token changed; conf={confidence:0.00}"
                        : $"OCR-like token stable; conf={confidence:0.00}";

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
                        DebugReason = reason
                    });

                    lastToken = token;
                }
                catch (Exception ex)
                {
                    _log(new TimelineEvent { Source = TimelineSource.Trigger, Severity = TimelineSeverity.Warn, Message = $"[SmartRules/OCR] fail-soft error: {ex.Message}" });
                }

                await Task.Delay(intervalMs, cts.Token).ConfigureAwait(false);
            }
        }, cts.Token);
    }

    private static string BuildCompactVisualToken(Bitmap bmp)
    {
        // Compact, privacy-preserving token (no raw image persistence).
        var cols = 16;
        var rows = 12;
        var sb = new System.Text.StringBuilder(cols * rows / 2 + 16);
        sb.Append("v1_");

        for (int gy = 0; gy < rows; gy++)
        {
            for (int gx = 0; gx < cols; gx++)
            {
                var x0 = gx * bmp.Width / cols;
                var y0 = gy * bmp.Height / rows;
                var x1 = Math.Max(x0 + 1, (gx + 1) * bmp.Width / cols);
                var y1 = Math.Max(y0 + 1, (gy + 1) * bmp.Height / rows);

                long sum = 0;
                int count = 0;
                for (int y = y0; y < y1; y += Math.Max(1, (y1 - y0) / 3))
                {
                    for (int x = x0; x < x1; x += Math.Max(1, (x1 - x0) / 3))
                    {
                        var c = bmp.GetPixel(Math.Min(x, bmp.Width - 1), Math.Min(y, bmp.Height - 1));
                        sum += (c.R + c.G + c.B) / 3;
                        count++;
                    }
                }

                var avg = count > 0 ? (int)(sum / count) : 0;
                sb.Append((avg / 32).ToString("x"));
            }
        }

        return sb.ToString();
    }

    public void Stop(string ruleId)
    {
        CancellationTokenSource? cts = null;
        lock (_lock)
        {
            if (_watchers.TryGetValue(ruleId, out cts))
                _watchers.Remove(ruleId);
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

        _roi.OnObservation += obs => OnObservation?.Invoke(obs);
        _uia.OnObservation += obs => OnObservation?.Invoke(obs);
        _ocr.OnObservation += obs => OnObservation?.Invoke(obs);
    }

    public event Action<Observation>? OnObservation;

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

    public void Dispose()
    {
        _roi.Dispose();
        _uia.Dispose();
        _ocr.Dispose();
    }
}
