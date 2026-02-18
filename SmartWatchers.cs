using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Text;
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
    private readonly BlockingCollection<IUiaCommand> _commands = new();
    private readonly Dictionary<string, UiaSession> _sessions = new();
    private readonly Thread _worker;

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
        _commands.Add(new StartCommand(rule, externalCt));
    }

    public void Stop(string ruleId)
    {
        _commands.Add(new StopCommand(ruleId));
    }

    public bool IsRunning(string ruleId)
    {
        lock (_sessions) return _sessions.ContainsKey(ruleId);
    }

    private void WorkerLoop()
    {
        while (!_commands.IsCompleted)
        {
            try
            {
                if (_commands.TryTake(out var cmd, 200))
                    ApplyCommand(cmd);

                PollSessions();
            }
            catch (Exception ex)
            {
                _log(new TimelineEvent
                {
                    Source = TimelineSource.Trigger,
                    Severity = TimelineSeverity.Warn,
                    Message = $"[SmartRules/UIA] fail-soft error: {ex.Message}"
                });
            }
        }
    }

    private void ApplyCommand(IUiaCommand command)
    {
        switch (command)
        {
            case StartCommand start:
            {
                lock (_sessions)
                {
                    _sessions[start.Rule.Id] = new UiaSession(start.Rule, start.ExternalCt);
                }

                _log(new TimelineEvent
                {
                    Source = TimelineSource.Trigger,
                    Message = $"[SmartRules/UIA] Subscribed ruleId={start.Rule.Id} selector={start.Rule.Smart.UiaSelectorRecipe}"
                });
                break;
            }
            case StopCommand stop:
            {
                bool removed;
                lock (_sessions) removed = _sessions.Remove(stop.RuleId);
                if (removed)
                {
                    _log(new TimelineEvent
                    {
                        Source = TimelineSource.Trigger,
                        Message = $"[SmartRules/UIA] Unsubscribed ruleId={stop.RuleId}"
                    });
                }

                break;
            }
        }
    }

    private void PollSessions()
    {
        List<UiaSession> snapshot;
        lock (_sessions) snapshot = _sessions.Values.ToList();

        foreach (var session in snapshot)
        {
            if (session.ExternalCt.IsCancellationRequested)
            {
                Stop(session.RuleId);
                continue;
            }

            if ((DateTime.UtcNow - session.LastPollUtc).TotalMilliseconds < Math.Max(100, session.Rule.Smart.UiaPollingMs))
                continue;

            session.LastPollUtc = DateTime.UtcNow;
            var obs = BuildObservation(session);
            if (obs is null)
                continue;

            if (!string.Equals(session.LastSignature, obs.Payload.GetValueOrDefault("signature", ""), StringComparison.Ordinal))
            {
                session.LastSignature = obs.Payload.GetValueOrDefault("signature", "");
                OnObservation?.Invoke(obs);
            }
        }
    }

    private Observation? BuildObservation(UiaSession session)
    {
        try
        {
            var window = FindTargetWindow(session.Rule);
            if (window is null)
            {
                return new Observation
                {
                    SourceType = ObservationSourceType.Uia,
                    Scope = $"rule:{session.RuleId}:uia",
                    TimestampUtc = DateTime.UtcNow,
                    Confidence = 0.0,
                    Payload = new Dictionary<string, string> { ["state"] = "window_not_found", ["signature"] = "window:none" },
                    DebugReason = "UIA target window not found."
                };
            }

            var selector = UiaSelectorRecipe.Parse(session.Rule.Smart.UiaSelectorRecipe);
            var matched = FindMatchingElement(window, selector);
            if (matched is null)
            {
                return new Observation
                {
                    SourceType = ObservationSourceType.Uia,
                    Scope = $"rule:{session.RuleId}:uia",
                    TimestampUtc = DateTime.UtcNow,
                    Confidence = 0.10,
                    Payload = new Dictionary<string, string>
                    {
                        ["state"] = "element_not_found",
                        ["selector"] = selector.ToCompactString(),
                        ["signature"] = $"missing:{selector.ToCompactString()}"
                    },
                    DebugReason = "UIA element missing for selector recipe."
                };
            }

            var name = SafeGet(() => matched.Current.Name) ?? "";
            var aid = SafeGet(() => matched.Current.AutomationId) ?? "";
            var enabled = SafeGet(() => matched.Current.IsEnabled) ? "1" : "0";
            var rect = SafeGet(() => matched.Current.BoundingRectangle);
            var controlType = SafeGet(() => matched.Current.ControlType.ProgrammaticName) ?? "unknown";
            var signature = $"{aid}|{name}|{enabled}|{rect.Left:0},{rect.Top:0},{rect.Width:0},{rect.Height:0}|{controlType}";

            double confidence = 0.40;
            if (!string.IsNullOrWhiteSpace(aid) && selector.AutomationId is not null && aid.Equals(selector.AutomationId, StringComparison.Ordinal)) confidence += 0.20;
            if (!string.IsNullOrWhiteSpace(name) && selector.NameContains is not null && name.Contains(selector.NameContains, StringComparison.OrdinalIgnoreCase)) confidence += 0.20;
            if (!string.IsNullOrWhiteSpace(controlType) && selector.ControlTypeContains is not null && controlType.Contains(selector.ControlTypeContains, StringComparison.OrdinalIgnoreCase)) confidence += 0.20;
            confidence = Math.Clamp(confidence, 0.0, 1.0);

            return new Observation
            {
                SourceType = ObservationSourceType.Uia,
                Scope = $"rule:{session.RuleId}:uia",
                TimestampUtc = DateTime.UtcNow,
                Confidence = confidence,
                Payload = new Dictionary<string, string>
                {
                    ["state"] = "element_found",
                    ["name"] = name,
                    ["automationId"] = aid,
                    ["enabled"] = enabled,
                    ["controlType"] = controlType,
                    ["selector"] = selector.ToCompactString(),
                    ["signature"] = signature
                },
                DebugReason = $"UIA element found '{name}' ({controlType}) enabled={enabled}."
            };
        }
        catch (ElementNotAvailableException)
        {
            return new Observation
            {
                SourceType = ObservationSourceType.Uia,
                Scope = $"rule:{session.RuleId}:uia",
                TimestampUtc = DateTime.UtcNow,
                Confidence = 0.0,
                Payload = new Dictionary<string, string> { ["state"] = "element_invalid", ["signature"] = "uia:invalid" },
                DebugReason = "UIA element became invalid; rebinding on next poll."
            };
        }
        catch (Exception ex)
        {
            _log(new TimelineEvent
            {
                Source = TimelineSource.Trigger,
                Severity = TimelineSeverity.Warn,
                Message = $"[SmartRules/UIA] poll fail-soft: {ex.Message}"
            });
            return null;
        }
    }

    private static AutomationElement? FindTargetWindow(RuleModel rule)
    {
        var windows = AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition);
        for (int i = 0; i < windows.Count; i++)
        {
            var candidate = windows[i];
            var title = SafeGet(() => candidate.Current.Name) ?? string.Empty;
            var className = SafeGet(() => candidate.Current.ClassName) ?? string.Empty;
            var pid = SafeGet(() => candidate.Current.ProcessId);

            if (!MatchesWindowFilter(rule, pid, title, className))
                continue;

            return candidate;
        }

        return null;
    }

    private static bool MatchesWindowFilter(RuleModel rule, int pid, string title, string className)
    {
        if (!rule.Trigger.UseWindowFilter)
            return true;

        var processName = "";
        try { processName = Process.GetProcessById(pid).ProcessName; } catch { }

        bool processOk = string.IsNullOrWhiteSpace(rule.Trigger.WindowProcessName) ||
                         processName.Equals(rule.Trigger.WindowProcessName, StringComparison.OrdinalIgnoreCase);
        bool titleOk = string.IsNullOrWhiteSpace(rule.Trigger.WindowTitleContains) ||
                       title.Contains(rule.Trigger.WindowTitleContains, StringComparison.OrdinalIgnoreCase);
        bool classOk = string.IsNullOrWhiteSpace(rule.Trigger.WindowClassName) ||
                       className.Equals(rule.Trigger.WindowClassName, StringComparison.OrdinalIgnoreCase);

        return rule.Trigger.WindowMatchMode switch
        {
            WindowMatchMode.ProcessOnly => processOk,
            WindowMatchMode.ProcessAndTitle => processOk && titleOk,
            WindowMatchMode.ProcessAndClass => processOk && classOk,
            WindowMatchMode.StrictAll => processOk && titleOk && classOk,
            _ => processOk
        };
    }

    private static AutomationElement? FindMatchingElement(AutomationElement window, UiaSelectorRecipe selector)
    {
        var conditions = new List<Condition>();
        if (!string.IsNullOrWhiteSpace(selector.AutomationId))
            conditions.Add(new PropertyCondition(AutomationElement.AutomationIdProperty, selector.AutomationId));
        if (!string.IsNullOrWhiteSpace(selector.ControlTypeContains))
        {
            var ct = ResolveControlType(selector.ControlTypeContains!);
            if (ct is not null)
                conditions.Add(new PropertyCondition(AutomationElement.ControlTypeProperty, ct));
        }

        var seed = conditions.Count == 0
            ? window.FindAll(TreeScope.Descendants, Condition.TrueCondition)
            : window.FindAll(TreeScope.Descendants, new AndCondition(conditions.ToArray()));

        for (int i = 0; i < seed.Count; i++)
        {
            var item = seed[i];
            var name = SafeGet(() => item.Current.Name) ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(selector.NameContains) && !name.Contains(selector.NameContains, StringComparison.OrdinalIgnoreCase))
                continue;
            return item;
        }

        return null;
    }

    private static ControlType? ResolveControlType(string raw)
    {
        if (raw.Contains("button", StringComparison.OrdinalIgnoreCase)) return ControlType.Button;
        if (raw.Contains("text", StringComparison.OrdinalIgnoreCase)) return ControlType.Text;
        if (raw.Contains("edit", StringComparison.OrdinalIgnoreCase)) return ControlType.Edit;
        if (raw.Contains("list", StringComparison.OrdinalIgnoreCase)) return ControlType.List;
        if (raw.Contains("item", StringComparison.OrdinalIgnoreCase)) return ControlType.ListItem;
        if (raw.Contains("window", StringComparison.OrdinalIgnoreCase)) return ControlType.Window;
        return null;
    }

    private static T SafeGet<T>(Func<T> getter)
    {
        try { return getter(); }
        catch { return default!; }
    }

    public void Dispose()
    {
        _commands.CompleteAdding();
        try { _worker.Join(TimeSpan.FromSeconds(1)); } catch { }
        _commands.Dispose();
    }

    private interface IUiaCommand;
    private sealed record StartCommand(RuleModel Rule, CancellationToken ExternalCt) : IUiaCommand;
    private sealed record StopCommand(string RuleId) : IUiaCommand;

    private sealed class UiaSession
    {
        public UiaSession(RuleModel rule, CancellationToken externalCt)
        {
            Rule = rule;
            RuleId = rule.Id;
            ExternalCt = externalCt;
        }

        public RuleModel Rule { get; }
        public string RuleId { get; }
        public CancellationToken ExternalCt { get; }
        public DateTime LastPollUtc { get; set; }
        public string LastSignature { get; set; } = "";
    }

    private sealed class UiaSelectorRecipe
    {
        public string? AutomationId { get; init; }
        public string? NameContains { get; init; }
        public string? ControlTypeContains { get; init; }

        public static UiaSelectorRecipe Parse(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return new UiaSelectorRecipe();

            var chunks = raw.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var chunk in chunks)
            {
                var idx = chunk.IndexOf('=');
                if (idx < 1 || idx >= chunk.Length - 1) continue;
                dict[chunk[..idx].Trim()] = chunk[(idx + 1)..].Trim();
            }

            return new UiaSelectorRecipe
            {
                AutomationId = dict.GetValueOrDefault("automationid") ?? dict.GetValueOrDefault("aid"),
                NameContains = dict.GetValueOrDefault("name") ?? dict.GetValueOrDefault("contains"),
                ControlTypeContains = dict.GetValueOrDefault("control") ?? dict.GetValueOrDefault("controltype")
            };
        }

        public string ToCompactString()
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(AutomationId)) sb.Append($"aid={AutomationId};");
            if (!string.IsNullOrWhiteSpace(NameContains)) sb.Append($"name={NameContains};");
            if (!string.IsNullOrWhiteSpace(ControlTypeContains)) sb.Append($"control={ControlTypeContains};");
            return sb.ToString();
        }
    }
}

public sealed class OcrWatcher : IWatcher
{
    private readonly Action<TimelineEvent> _log;
    private readonly object _lock = new();
    private readonly Dictionary<string, CancellationTokenSource> _watchers = new();
    private readonly HashSet<string> _providerMissingNotified = new();

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
            var intervalMs = Math.Max(120, rule.Smart.OcrPollingMs > 0 ? rule.Smart.OcrPollingMs : 1000 / Math.Max(1, rule.Trigger.SamplingHz));
            string? lastNormalized = null;

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

                    var text = TryReadTextFromRoi(roi, out var provider);
                    if (text is null)
                    {
                        NotifyProviderMissingOnce(rule.Id, provider);
                        await Task.Delay(intervalMs, cts.Token).ConfigureAwait(false);
                        continue;
                    }

                    var normalized = NormalizeText(text, rule.Smart);
                    var confidence = OcrTextMatcher.MatchConfidence(normalized, rule.Smart);
                    var changed = !string.Equals(lastNormalized, normalized, StringComparison.Ordinal);
                    lastNormalized = normalized;

                    OnObservation?.Invoke(new Observation
                    {
                        SourceType = ObservationSourceType.Ocr,
                        Scope = $"rule:{rule.Id}:ocr",
                        TimestampUtc = DateTime.UtcNow,
                        Confidence = confidence,
                        Payload = new Dictionary<string, string>
                        {
                            ["provider"] = provider,
                            ["text"] = normalized,
                            ["changed"] = changed ? "1" : "0"
                        },
                        DebugReason = changed
                            ? $"OCR text changed; confidence={confidence:0.00}"
                            : $"OCR text stable; confidence={confidence:0.00}"
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

    private void NotifyProviderMissingOnce(string ruleId, string provider)
    {
        lock (_providerMissingNotified)
        {
            if (!_providerMissingNotified.Add(ruleId)) return;
        }

        _log(new TimelineEvent
        {
            Source = TimelineSource.Trigger,
            Severity = TimelineSeverity.Warn,
            Message = $"[SmartRules/OCR] No OCR provider available for ruleId={ruleId}. Checked: {provider}. Install local tesseract.exe for OCR support."
        });
    }

    private static string NormalizeText(string raw, SmartRuleSettings settings)
    {
        var value = settings.OcrTrim ? raw.Trim() : raw;
        if (settings.OcrCaseInsensitive)
            value = value.ToLowerInvariant();

        return value;
    }

    private static string? TryReadTextFromRoi(RoiRect roi, out string provider)
    {
        var tesseract = FindTesseractExe();
        if (tesseract is null)
        {
            provider = "tesseract:not-found";
            return null;
        }

        provider = "tesseract";
        using var bmp = new Bitmap(Math.Max(1, roi.W), Math.Max(1, roi.H), PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(roi.X, roi.Y, 0, 0, bmp.Size, CopyPixelOperation.SourceCopy);
        }

        var baseName = Path.Combine(Path.GetTempPath(), $"boltmacro_ocr_{Guid.NewGuid():N}");
        var imagePath = baseName + ".png";
        var outputBase = baseName + "_out";

        try
        {
            bmp.Save(imagePath, ImageFormat.Png);

            var psi = new ProcessStartInfo
            {
                FileName = tesseract,
                Arguments = $"\"{imagePath}\" \"{outputBase}\" --psm 6",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };

            using var proc = Process.Start(psi);
            if (proc is null)
                return null;
            if (!proc.WaitForExit(2500))
            {
                try { proc.Kill(true); } catch { }
                return null;
            }

            var outPath = outputBase + ".txt";
            if (!File.Exists(outPath))
                return null;

            return File.ReadAllText(outPath);
        }
        finally
        {
            TryDelete(imagePath);
            TryDelete(outputBase + ".txt");
            TryDelete(outputBase + ".osd");
        }
    }

    private static string? FindTesseractExe()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(dir, "tesseract.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch
            {
            }
        }

        return null;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
        }
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

    public void Dispose()
    {
        _roi.Dispose();
        _uia.Dispose();
        _ocr.Dispose();
    }

    private void ForwardObservation(Observation observation)
    {
        var parts = observation.Scope.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
            return;

        var ruleId = parts[1];
        OnObservation?.Invoke(ruleId, observation);
    }
}
