# Smart Rules Architecture (Stage 1)

## Components

- **WatcherCoordinator** routes all sensor emissions through a single observation bus.
- **RoiDiffWatcher** wraps the existing deterministic ROI metric monitor unchanged.
- **UiaWatcher** runs on one dedicated MTA thread, serializes subscribe/unsubscribe work, and polls UI Automation state for selector-bound elements.
- **OcrWatcher** performs local OCR on selected regions using an offline provider (`tesseract.exe` if present) and stores only compact text/score metadata.
- **SmartRuleEvaluator** combines sensor confidence into `RuleScore` and explanation (top reasons), then applies stability + cooldown decision policy.
- **Controller** remains the deterministic action engine (`Observation -> Score -> Decision -> Macro execution`).

## Threading / lifecycle

1. Arm rule.
2. Controller starts watchers via `WatcherCoordinator`.
3. Watchers emit `Observation` with scoped identifiers (`rule:{id}:sensor`).
4. Controller updates runtime state cache and evaluates score.
5. If score policy permits fire, macro execution uses the existing lease/panic/cancel-safe pipeline.
6. On disarm/stop/panic, all watchers stop fail-soft.

### UIA threading guarantee

`UiaWatcher` keeps all session mutations and polling on **one MTA worker thread**, preventing cross-thread UIA handler churn and making rebinding deterministic.

## Data flow

`Watcher -> Observation -> RuleRuntimeSignalState -> SmartRuleEvaluator -> RuleScore + Explanation -> DecisionPolicy -> HandleRuleFireAsync -> PlaybackService`

## Privacy defaults

- Monitoring runs only for explicitly armed rules with explicit ROI/context.
- No network calls.
- OCR writes no persistent screenshot files (temporary capture files are deleted immediately).
- Persistent diagnostics store compact metrics/scores/reasons only.

## Diagnostics examples

- `[SmartRules] ruleId=... src=RoiDiff score=0.731 why=ROI diff 0.731 vs threshold 0.120`
- `[SmartRules] ruleId=... src=Uia score=0.642 why=UIA element found 'Submit' (ControlType.Button) enabled=1.`
- `[SmartRules/OCR] No OCR provider available ... Install local tesseract.exe ...`
