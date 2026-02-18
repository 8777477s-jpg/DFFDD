# How to add a watcher plugin

1. Implement `IWatcher` in a new class:
   - `Start(RuleModel rule, CancellationToken token)`
   - `Stop(string ruleId)`
   - `IsRunning(string ruleId)`
   - emit `OnObservation` with normalized `Observation`.
2. Emit compact payloads by default (scores/tokens/hashes), no raw screenshots unless diagnostics mode is enabled.
3. Register watcher in `Controller` constructor with `SmartRuleEngine.RegisterWatcher(...)`.
4. Add per-rule toggles/params to `SmartRuleConfig`.
5. Bind UI controls in `MainForm` (show/save fields).
6. If watcher has global switch, add an `AppSettings` toggle and persist via `SettingsStore`.
7. Add timeline logs for start/stop/errors; watcher failures must be fail-soft.

## Payload recommendations
- `SourceType` identifies watcher class.
- `Scope` identifies window/process/roi.
- `Confidence` normalized to [0..1].
- `DebugReason` concise, human-readable explanation for timeline and score history.
