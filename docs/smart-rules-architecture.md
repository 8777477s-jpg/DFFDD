# Smart Rules Architecture Note (Stage 1)

## Components
- **Watchers (sensor plugins)**: `RoiDiffWatcher`, `UiaWatcher`, `OcrWatcher` implement `IWatcher` and emit `Observation` events.
- **Rule evaluator**: `RuleEvaluator` converts latest observations into a score and top reasons.
- **Decision policy**: controller-side streak + threshold gating (`FireThreshold`, `ScoreConsecutiveRequired`) before deterministic macro execution.
- **Deterministic executor**: existing `PlaybackService` + input lease + panic stop remain unchanged.
- **Persistence**: SQLite stores bounded score history (`rule_scores`) and user feedback (`rule_feedback`).

## Threads
- **Main UI thread**: WinForms controls and user interaction.
- **ROI monitoring thread(s)**: existing ROI monitor task loop (unchanged trigger behavior).
- **UIA watcher thread**: dedicated MTA thread per rule watcher loop for serialized UI Automation access.
- **OCR watcher task**: periodic polling task on background thread, scoped to explicit region metadata.

## Rule lifecycle
1. User arms rule.
2. Controller starts ROI watcher + optional UIA/OCR watchers.
3. Watchers emit observations.
4. ROI trigger observation drives scoring with latest multi-signal context.
5. Decision policy checks threshold/stability.
6. If accepted, controller acquires lease and runs macro deterministically.
7. Rule returns to monitoring (or disarms by repeat policy).
8. User feedback adjusts threshold/weights safely.

## Data flow
`Observation (ROI/UIA/OCR) -> RuleEvaluator(score+top reasons) -> Decision policy(streak/debounce/cooldown) -> Action(PlaybackService)`.

## Privacy & retention defaults
- Monitoring requires explicit rule configuration (ROI/UIA selector/OCR region).
- Raw image persistence is off by default (OCR watcher stores tokens/metadata only).
- Score history is bounded per-rule.
- Sensor errors fail-soft and generate timeline diagnostics.

## Sample timeline diagnostics
- `[SmartRule] score=0.812 threshold=0.650 reasons=ROI confidence=1.00 (...); UIA confidence=0.72 ...`
- `[SmartRule] feedback=FalseTrigger rule=SubmitButtonWatch newThreshold=0.68`
- `[Trigger] MonitorResume ruleId=...`

## How to add a new watcher plugin
1. Implement `IWatcher` (`Start`, `Stop`, `IsRunning`, `OnObservation`).
2. Emit `Observation` with:
   - `SourceType`
   - scoped identifiers in `Scope`
   - compact `Payload`
   - `Confidence` and concise `DebugReason`
3. Register watcher in `Program.cs` and `Controller` constructor wiring.
4. Add rule settings fields/toggles in `RoiTrigger` + `MainForm` editor.
5. Add evaluator weight/logic in `RuleEvaluator`.
6. Ensure fail-soft behavior (catch, emit degraded observation, continue).
