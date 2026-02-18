# How to add a new watcher plugin

1. Implement `IWatcher`:
   - `Start(RuleModel rule, CancellationToken externalCt)`
   - `Stop(string ruleId)`
   - `IsRunning(string ruleId)`
   - `event Action<Observation> OnObservation`
2. Emit `Observation` with:
   - source type (`ObservationSourceType`)
   - stable scope (`rule:{id}:sensor`)
   - confidence `[0..1]`
   - compact payload only (no raw files by default)
   - short human-readable `DebugReason`
3. Register watcher in `Program.cs` and inject into `WatcherCoordinator`.
4. Add per-rule settings into `SmartRuleSettings` and persist through `rules.smart_json`.
5. Keep failure mode soft:
   - catch provider exceptions,
   - log timeline warnings,
   - never crash controller/runtime.

## Reference behavior from current watchers

- `RoiDiffWatcher`: wraps existing trigger metric loop.
- `UiaWatcher`: single MTA thread; deterministic subscription/polling sequence.
- `OcrWatcher`: local OCR provider probing (`tesseract.exe`) with immediate temp-file cleanup.
