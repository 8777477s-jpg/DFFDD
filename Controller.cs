namespace BoltMacro;

public sealed class Controller : IDisposable
{
    private readonly Storage _storage;
    private readonly TimelineService _timeline;
    private readonly InputLeaseManager _lease;
    private readonly RecorderService _recorder;
    private readonly PlaybackService _player;
    private readonly RoiMonitorService _roiMonitor;
    private readonly SmartRuleEngine _smartEngine;
    private readonly RoiDiffWatcher _roiWatcher;
    private readonly UiaWatcher _uiaWatcher;
    private readonly OcrWatcher _ocrWatcher;

    private readonly CancellationTokenSource _appCts = new();

    private List<MacroStep>? _recordedDraftSteps;
    private CancellationTokenSource? _activeRunCts;

    public AppMode Mode { get; private set; } = AppMode.Idle;

    public Controller(Storage storage, TimelineService timeline, InputLeaseManager lease,
        RecorderService recorder, PlaybackService player, RoiMonitorService roiMonitor, AppSettings settings)
    {
        _storage = storage;
        _timeline = timeline;
        _lease = lease;
        _recorder = recorder;
        _player = player;
        _roiMonitor = roiMonitor;
        _smartEngine = new SmartRuleEngine(storage, timeline);
        _roiWatcher = new RoiDiffWatcher(roiMonitor, timeline);
        _uiaWatcher = new UiaWatcher();
        _ocrWatcher = new OcrWatcher(settings);

        _roiWatcher.OnTriggered += (rid, metric) => _ = HandleRoiTriggeredAsync(rid, metric);

        _smartEngine.RegisterWatcher(_roiWatcher);
        _smartEngine.RegisterWatcher(_uiaWatcher);
        _smartEngine.RegisterWatcher(_ocrWatcher);
    }

    public void InitializeRuntimeRules()
    {
        // Safety-first startup: do not auto-resume armed/running rules.
        foreach (var r in _storage.ListRules())
        {
            if (r.Enabled || r.State != RuleState.Disarmed || r.RunsDone != 0)
            {
                r.Enabled = false;
                r.State = RuleState.Disarmed;
                r.RunsDone = 0;
                _storage.UpsertRule(r);
            }
        }

        Mode = AppMode.Idle;
        _timeline.Add(new TimelineEvent { Source = TimelineSource.System, Message = "Runtime initialized. All rules disarmed by safety policy." });
    }

    public bool StartRecording()
    {
        if (Mode != AppMode.Idle) return false;

        if (!_lease.TryAcquire(LeaseOwnerType.Recording, "recording"))
        {
            _timeline.Add(new TimelineEvent { Source = TimelineSource.Recording, Severity = TimelineSeverity.Warn, Message = "Cannot record: input lease busy." });
            return false;
        }

        bool ok = _recorder.Start();
        if (!ok)
        {
            _lease.Release(LeaseOwnerType.Recording, "recording");
            Mode = AppMode.Idle;
            _timeline.Add(new TimelineEvent { Source = TimelineSource.Recording, Severity = TimelineSeverity.Error, Message = "Recording did not start." });
            return false;
        }

        _recordedDraftSteps = null;
        Mode = AppMode.Recording;
        _timeline.Add(new TimelineEvent { Source = TimelineSource.Recording, Message = "Recording mode entered." });
        return true;
    }

    public void StopRecording()
    {
        if (Mode != AppMode.Recording) return;
        _recordedDraftSteps = _recorder.StopAndBuildSteps();
        Mode = AppMode.Idle;
        _timeline.Add(new TimelineEvent { Source = TimelineSource.Recording, Message = "Recording mode exited." });
    }

    public string? SaveRecordedMacro(string name)
    {
        if (_recordedDraftSteps is null) return null;

        try
        {
            var m = new MacroModel
            {
                Id = IdUtil.NewId(),
                Name = string.IsNullOrWhiteSpace(name) ? "Macro" : name,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                Steps = _recordedDraftSteps
            };
            _storage.UpsertMacro(m);
            _timeline.Add(new TimelineEvent { Source = TimelineSource.Recording, Message = $"Saved macro '{m.Name}'." });
            return m.Id;
        }
        finally
        {
            _recordedDraftSteps = null;
            _lease.Release(LeaseOwnerType.Recording, "recording");
            Mode = AppMode.Idle;
        }
    }

    public void DiscardRecordedMacro()
    {
        _recordedDraftSteps = null;
        _lease.Release(LeaseOwnerType.Recording, "recording");
        Mode = AppMode.Idle;
        _timeline.Add(new TimelineEvent { Source = TimelineSource.Recording, Severity = TimelineSeverity.Warn, Message = "Discarded recorded macro." });
    }

    public async Task RunMacroManualAsync(string macroId)
    {
        if (Mode == AppMode.Recording) return;

        var macro = _storage.GetMacro(macroId);
        if (macro is null) return;

        if (!_lease.TryAcquire(LeaseOwnerType.ManualMacroRun, macroId))
        {
            _timeline.Add(new TimelineEvent { Source = TimelineSource.Playback, Severity = TimelineSeverity.Warn, Message = "Lease busy. Cannot run macro now." });
            return;
        }

        _activeRunCts = new CancellationTokenSource();
        Mode = AppMode.Playing;

        try
        {
            var run = new RunRecord { RuleId = null, MacroId = macroId, StartUtc = DateTime.UtcNow };
            _storage.InsertRunStart(run);

            _timeline.Add(new TimelineEvent { Source = TimelineSource.Playback, Message = $"Macro start: {macro.Name}" });

            var (_, status, error) = await _player.RunAsync(macro.Steps, _activeRunCts.Token);

            run.EndUtc = DateTime.UtcNow;
            run.Status = status;
            run.ErrorText = error;
            _storage.UpdateRunEnd(run);

            _timeline.Add(new TimelineEvent
            {
                Source = TimelineSource.Playback,
                Severity = status == RunStatus.Success ? TimelineSeverity.Info : TimelineSeverity.Warn,
                Message = $"Macro end: {macro.Name} ({status})"
            });
        }
        finally
        {
            _activeRunCts?.Cancel();
            _activeRunCts?.Dispose();
            _activeRunCts = null;

            _lease.ForceRelease();
            RecomputeMode();
        }
    }

    public void ArmRule(string ruleId)
    {
        if (Mode == AppMode.Recording) return;

        _timeline.Add(new TimelineEvent { Source = TimelineSource.Rule, Message = $"[Rule] ArmRequested ruleId={ruleId}" });

        // Phase-1 deterministic policy: one visual rule monitor at a time.
        foreach (var other in _storage.ListRules())
        {
            if (other.Id == ruleId) continue;
            if (!other.Enabled && other.State == RuleState.Disarmed) continue;

            _roiWatcher.Stop(other.Id);
            _uiaWatcher.Stop(other.Id);
            _ocrWatcher.Stop(other.Id);
            other.Enabled = false;
            other.State = RuleState.Disarmed;
            _storage.UpsertRule(other);
            _timeline.Add(new TimelineEvent { Source = TimelineSource.Rule, Severity = TimelineSeverity.Warn, Message = $"Disarmed '{other.Name}' due to single-monitor policy." });
        }

        var rule = _storage.ListRules().FirstOrDefault(x => x.Id == ruleId);
        if (rule is null)
        {
            _timeline.Add(new TimelineEvent { Source = TimelineSource.Rule, Severity = TimelineSeverity.Error, Message = $"Arm failed: rule not found ({ruleId})." });
            return;
        }

        if (string.IsNullOrWhiteSpace(rule.MacroId))
        {
            _timeline.Add(new TimelineEvent { Source = TimelineSource.Rule, Severity = TimelineSeverity.Error, Message = $"Arm failed for '{rule.Name}': macro is not assigned." });
            return;
        }

        var macroForRule = _storage.GetMacro(rule.MacroId);
        if (macroForRule is null)
        {
            _timeline.Add(new TimelineEvent { Source = TimelineSource.Rule, Severity = TimelineSeverity.Error, Message = $"Arm failed for '{rule.Name}': macro '{rule.MacroId}' not found." });
            return;
        }

        bool hasLegacyRoi = rule.Trigger.Roi is not null && rule.Trigger.Roi.W > 5 && rule.Trigger.Roi.H > 5;
        bool hasContextButton = rule.Trigger.ContextRoi is not null && rule.Trigger.ButtonRoiRelative is not null;

        if (!hasLegacyRoi && !hasContextButton)
        {
            _timeline.Add(new TimelineEvent { Source = TimelineSource.Rule, Severity = TimelineSeverity.Error, Message = $"Cannot arm rule '{rule.Name}': ROI is missing." });
            return;
        }

        rule.Enabled = true;
        rule.State = RuleState.ArmedMonitoring;
        _storage.UpsertRule(rule);

        _roiWatcher.Start(rule, _appCts.Token);
        if (rule.Smart.UiaWatcherEnabled) _uiaWatcher.Start(rule, _appCts.Token);
        if (rule.Smart.OcrWatcherEnabled) _ocrWatcher.Start(rule, _appCts.Token);

        if (!_roiWatcher.IsRunning(rule.Id))
        {
            rule.Enabled = false;
            rule.State = RuleState.Disarmed;
            _storage.UpsertRule(rule);
            _timeline.Add(new TimelineEvent { Source = TimelineSource.Rule, Severity = TimelineSeverity.Error, Message = $"Failed to start monitor for '{rule.Name}'. Rule disarmed." });
            RecomputeMode();
            return;
        }

        _timeline.Add(new TimelineEvent { Source = TimelineSource.Rule, Message = $"[Rule] ArmRequested ruleId={rule.Id} macroId={rule.MacroId} state={rule.State} enabled={rule.Enabled}" });
        RecomputeMode();
    }

    public void DisarmRule(string ruleId)
    {
        _roiWatcher.Stop(ruleId);
        _uiaWatcher.Stop(ruleId);
        _ocrWatcher.Stop(ruleId);

        var rule = _storage.ListRules().FirstOrDefault(x => x.Id == ruleId);
        if (rule is null) return;

        rule.Enabled = false;
        rule.State = RuleState.Disarmed;
        _storage.UpsertRule(rule);

        _timeline.Add(new TimelineEvent { Source = TimelineSource.Rule, Message = $"Rule disarmed: {rule.Name}." });
        RecomputeMode();
    }

    private async Task HandleRoiTriggeredAsync(string ruleId, double metric)
    {
        var rule = _storage.ListRules().FirstOrDefault(x => x.Id == ruleId);
        if (rule is null) return;

        var snapshot = _smartEngine.Evaluate(rule);
        _timeline.Add(new TimelineEvent { Source = TimelineSource.Rule, Message = $"[SmartRule] rule={rule.Name} score={snapshot.Score:0.000} why={snapshot.Explanation}" });
        if (!_smartEngine.ShouldFire(rule, snapshot)) return;

        await HandleRuleFireAsync(ruleId, metric, "smart-score");
    }

    private async Task HandleRuleFireAsync(string ruleId, double metric, string reason)
    {
        try
        {
            var rule = _storage.ListRules().FirstOrDefault(x => x.Id == ruleId);
            if (rule is null)
            {
                _timeline.Add(new TimelineEvent { Source = TimelineSource.Rule, Severity = TimelineSeverity.Error, Message = $"Trigger dropped: rule not found ({ruleId})." });
                return;
            }
            if (!rule.Enabled || rule.State != RuleState.ArmedMonitoring)
            {
                _timeline.Add(new TimelineEvent { Source = TimelineSource.Rule, Severity = TimelineSeverity.Warn, Message = $"Trigger dropped for '{rule.Name}': state mismatch (Enabled={rule.Enabled}, State={rule.State})." });
                return;
            }
            if (rule.MacroId is null)
            {
                _timeline.Add(new TimelineEvent { Source = TimelineSource.Rule, Severity = TimelineSeverity.Error, Message = $"Trigger dropped for '{rule.Name}': MacroId is null." });
                return;
            }

            if (rule.Repeat.Mode == RepeatMode.Once && rule.RunsDone >= 1)
            {
                _timeline.Add(new TimelineEvent { Source = TimelineSource.Rule, Severity = TimelineSeverity.Info, Message = $"Trigger ignored for '{rule.Name}': repeat Once already executed." });
                return;
            }
            if (rule.Repeat.Mode == RepeatMode.RepeatN && rule.RunsDone >= rule.Repeat.N)
            {
                _timeline.Add(new TimelineEvent { Source = TimelineSource.Rule, Severity = TimelineSeverity.Info, Message = $"Trigger ignored for '{rule.Name}': RepeatN reached ({rule.RunsDone}/{rule.Repeat.N})." });
                return;
            }

            _timeline.Add(new TimelineEvent { Source = TimelineSource.Rule, Message = $"[Rule] RunAttempt ruleId={rule.Id} macroId={rule.MacroId} macroName=? reason=trigger metric={metric:0.000}" });

            if (!_lease.TryAcquire(LeaseOwnerType.RuleRun, ruleId))
            {
                _timeline.Add(new TimelineEvent { Source = TimelineSource.System, Severity = TimelineSeverity.Warn, Message = $"[Lease] Result owner=RuleRun:{ruleId} result=deny" });
                return;
            }

            _timeline.Add(new TimelineEvent { Source = TimelineSource.System, Message = $"[Lease] Result owner=RuleRun:{ruleId} result=ok" });

            _roiWatcher.Stop(ruleId);
            _uiaWatcher.Stop(ruleId);
            _ocrWatcher.Stop(ruleId);

            Mode = AppMode.RunningRule;
            _activeRunCts = new CancellationTokenSource();

            rule.State = RuleState.Running;
            _storage.UpsertRule(rule);

            var macro = _storage.GetMacro(rule.MacroId);
            if (macro is null)
            {
                rule.State = RuleState.Disarmed;
                rule.Enabled = false;
                _storage.UpsertRule(rule);
                _timeline.Add(new TimelineEvent { Source = TimelineSource.Rule, Severity = TimelineSeverity.Error, Message = "Rule triggered but macro not found. Rule disarmed." });
                return;
            }

            _timeline.Add(new TimelineEvent { Source = TimelineSource.Rule, Message = $"[Rule] RunAttempt ruleId={rule.Id} macroId={rule.MacroId} macroName={macro.Name} reason=trigger" });

            var run = new RunRecord { RuleId = ruleId, MacroId = rule.MacroId, StartUtc = DateTime.UtcNow };
            _storage.InsertRunStart(run);

            _timeline.Add(new TimelineEvent { Source = TimelineSource.Playback, Message = $"[Playback] Start ruleId={rule.Id} macroId={macro.Id}" });

            var (_, status, error) = await _player.RunAsync(macro.Steps, _activeRunCts.Token);

            run.EndUtc = DateTime.UtcNow;
            run.Status = status;
            run.ErrorText = error;
            _storage.UpdateRunEnd(run);

            rule.RunsDone += 1;

            _timeline.Add(new TimelineEvent
            {
                Source = TimelineSource.Playback,
                Severity = status == RunStatus.Success ? TimelineSeverity.Info : TimelineSeverity.Warn,
                Message = $"[Playback] End ruleId={rule.Id} status={status} error={(string.IsNullOrWhiteSpace(error) ? string.Empty : error)}"
            });

            bool shouldContinue =
                rule.Enabled &&
                (rule.Repeat.Mode == RepeatMode.Infinite ||
                 (rule.Repeat.Mode == RepeatMode.RepeatN && rule.RunsDone < rule.Repeat.N) ||
                 (rule.Repeat.Mode == RepeatMode.Once && rule.RunsDone < 1));

            rule.State = shouldContinue ? RuleState.ArmedMonitoring : RuleState.Disarmed;
            rule.Enabled = shouldContinue;
            _storage.UpsertRule(rule);
            _timeline.Add(new TimelineEvent { Source = TimelineSource.Rule, Message = $"[Rule] PostRun ruleId={rule.Id} nextState={rule.State} runsDone={rule.RunsDone}/{(rule.Repeat.Mode == RepeatMode.RepeatN ? rule.Repeat.N.ToString() : "inf")}" });

            if (shouldContinue)
            {
                _roiWatcher.Start(rule, _appCts.Token);
                if (rule.Smart.UiaWatcherEnabled) _uiaWatcher.Start(rule, _appCts.Token);
                if (rule.Smart.OcrWatcherEnabled) _ocrWatcher.Start(rule, _appCts.Token);
                _timeline.Add(new TimelineEvent { Source = TimelineSource.Trigger, Message = $"[Trigger] MonitorResume ruleId={rule.Id}" });
            }
            else
            {
                _timeline.Add(new TimelineEvent { Source = TimelineSource.Trigger, Message = $"[Trigger] MonitorStop ruleId={rule.Id}" });
            }
        }
        catch (Exception ex)
        {
            try
            {
                var rule = _storage.ListRules().FirstOrDefault(x => x.Id == ruleId);
                if (rule is not null)
                {
                    rule.Enabled = false;
                    rule.State = RuleState.Disarmed;
                    _storage.UpsertRule(rule);
                }
            }
            catch
            {
            }
            _timeline.Add(new TimelineEvent { Source = TimelineSource.Rule, Severity = TimelineSeverity.Error, Message = $"[Error] TriggerCallbackException ruleId={ruleId} ex={ex.Message}" });
        }
        finally
        {
            _activeRunCts?.Cancel();
            _activeRunCts?.Dispose();
            _activeRunCts = null;

            _lease.ForceRelease();
            RecomputeMode();
        }
    }

    public void MarkTriggerFeedback(string ruleId, bool correct)
    {
        var rule = _storage.ListRules().FirstOrDefault(x => x.Id == ruleId);
        if (rule is null) return;
        _smartEngine.ApplyFeedback(rule, correct);
        _storage.UpsertRule(rule);
        _timeline.Add(new TimelineEvent
        {
            Source = TimelineSource.Rule,
            Severity = correct ? TimelineSeverity.Info : TimelineSeverity.Warn,
            Message = correct
                ? $"Feedback accepted for '{rule.Name}': correct trigger (threshold={rule.Smart.FireThreshold:0.00}, bias={rule.Smart.AdaptiveBias:0.00})."
                : $"Feedback accepted for '{rule.Name}': false trigger (threshold={rule.Smart.FireThreshold:0.00}, bias={rule.Smart.AdaptiveBias:0.00})."
        });
    }

    private static string FormatRepeatLabel(RuleModel rule)
        => rule.Repeat.Mode switch
        {
            RepeatMode.Once => "once",
            RepeatMode.RepeatN => rule.Repeat.N.ToString(),
            RepeatMode.Infinite => "inf",
            _ => "unknown"
        };

    public void PanicStop()
    {
        _timeline.Add(new TimelineEvent { Source = TimelineSource.System, Severity = TimelineSeverity.Warn, Message = "Panic stop requested." });

        try { _activeRunCts?.Cancel(); } catch { }
        _roiMonitor.StopAll();

        try { _recorder.StopAndBuildSteps(); } catch { }

        _lease.ForceRelease();

        foreach (var r in _storage.ListRules())
        {
            r.Enabled = false;
            r.State = RuleState.Disarmed;
            r.RunsDone = 0;
            _storage.UpsertRule(r);
        }

        _recordedDraftSteps = null;
        Mode = AppMode.Idle;
        _timeline.Add(new TimelineEvent { Source = TimelineSource.System, Message = "Panic stop completed" });
    }

    private void RecomputeMode()
    {
        if (Mode == AppMode.Recording) return;
        if (_activeRunCts is not null && !_activeRunCts.IsCancellationRequested)
        {
            Mode = AppMode.RunningRule;
            return;
        }

        var anyArmed = _storage.ListRules().Any(r => r.Enabled && r.State == RuleState.ArmedMonitoring);
        Mode = anyArmed ? AppMode.Monitoring : AppMode.Idle;
    }

    public void Dispose()
    {
        PanicStop();
        _appCts.Cancel();
        _roiWatcher.Dispose();
        _uiaWatcher.Dispose();
        _ocrWatcher.Dispose();
        _appCts.Dispose();
    }
}
