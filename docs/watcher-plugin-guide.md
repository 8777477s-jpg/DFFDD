# How to add a new watcher plugin

1. Implement `IWatcher`:
   - `Start(RuleModel rule, CancellationToken externalCt)`
   - `Stop(string ruleId)`
   - `IsRunning(string ruleId)`
   - `event Action<Observation> OnObservation`
2. Emit `Observation` with:
   - `SourceType` enum value
   - stable `Scope` (`rule:{id}:sensor`) 
   - confidence `[0..1]`
   - concise `DebugReason`
   - minimal payload metadata (avoid raw images by default)
3. Register watcher in `Program.cs` and wire through `WatcherCoordinator`.
4. Add rule settings fields in `SmartRuleSettings` for toggles/config.
5. Surface settings in `MainForm` editor and persist via `Storage.UpsertRule`.
6. Keep fail-soft behavior: never throw from sensor loops; timeline-log warnings instead.

## Example timeline diagnostics

- `[SmartRules] ruleId=abc src=RoiDiff score=0.812 why=ROI diff 0.812 vs threshold 0.120`
- `[SmartRules/UIA] Subscribed ruleId=abc selector=AutomationId=Submit + ControlType=Button`
- `[SmartRules/OCR] fail-soft error: ...`
