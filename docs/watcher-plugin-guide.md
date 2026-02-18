# How to add a new watcher plugin

1. Implement `IWatcher`:
   - `Start(RuleModel rule, CancellationToken externalCt)`
   - `Stop(string ruleId)`
   - `IsRunning(string ruleId)`
   - `event Action<Observation> OnObservation`
2. Emit `Observation` with:
   - `SourceType`
   - stable scope (`rule:{id}:<sensor>`)
   - confidence `[0..1]`
   - concise explainability reason (`DebugReason`)
   - compact payload (privacy-first)
3. Register watcher in `Program.cs` and add it to `WatcherCoordinator`.
4. Add settings to `SmartRuleSettings` and bind in `MainForm`.
5. Persist settings through `Storage` rule `smart_json`.
6. Fail-soft rule: sensor exceptions must be caught and timeline-logged.

## Recommended selector recipe for UIA watcher

Use multi-anchor format in `UiaSelectorRecipe`:

`automationId=SubmitBtn;controlType=Button;nameContains=Submit;parentName=Checkout`

Supported keys: `automationId`, `controlType`, `nameContains`, `className`, `processName`, `parentName`.

## Example timeline messages

- `[SmartRules] ruleId=abc src=RoiDiff score=0.79 why=RoiDiff: ROI diff ...`
- `[SmartRules/UIA] Subscribed ruleId=abc selector=automationId=...`
- `[SmartRules/OCR] fail-soft error: ...`
