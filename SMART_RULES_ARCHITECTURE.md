# Smart Rules Architecture (Stage 1)

## Components
- **Watchers (sensor layer):** `RoiDiffWatcher`, `UiaWatcher`, `OcrWatcher` implement `IWatcher` and emit normalized `Observation` objects.
- **Orchestrator:** `SmartRulesOrchestrator` owns watcher lifecycle per rule, keeps bounded observation windows, evaluates scores, applies decision policy, and calls deterministic macro execution through the existing controller callback.
- **Evaluator:** `RuleEvaluator` converts recent observations into `RuleScore` + short explanation (top contributors).
- **Persistence:** `Storage` now stores bounded score history (`rule_scores`) and feedback events (`rule_feedback`).

## Threading model
- ROI monitoring keeps the existing monitor loop behavior in `RoiMonitorService` (fast L1 path).
- UIA operations are serialized via one dedicated **MTA thread** (`BoltMacro.UIA.MTA`) that sequences start/stop requests and avoids cross-thread handler churn.
- OCR polling is on per-rule background tasks with cancellation tokens.
- All watcher failures are fail-soft and emitted to Timeline as warnings.

## Rule lifecycle
1. User arms a rule.
2. Controller starts Smart Rules orchestration (ROI always; UIA/OCR gated by settings/rule).
3. Watchers emit observations (`Observation` includes source, scope, confidence, debug reason, minimal payload).
4. Evaluator computes score + explanation.
5. Decision policy checks threshold, consecutive passes, and cooldown.
6. On fire, deterministic existing macro runner executes.
7. Rule returns to monitor state or disarms by repeat policy.

## Data flow
`Observation -> RuleEvaluator (score/explanation) -> DecisionPolicy -> Controller.HandleRuleFireAsync -> PlaybackService`.

## Privacy defaults
- Monitoring is scoped to selected rule/window/ROI.
- Only compact signal data are persisted by default (scores, tokens, reasons).
- No raw screenshot files are written in default mode.
- Diagnostics remains opt-in through settings.

## Example timeline diagnostics
- `[SmartRules] Monitoring ON ruleId=... (ROI/UIA/OCR active where enabled).`
- `[SmartRules] score ruleId=... score=0.741 why=ROI_DIFF:0.89 (... ) | UIA:0.60 (... )`
- `[SmartRules] Feedback FalseTrigger recorded for 'RuleA'. threshold=0.72`

## How to add a new watcher plugin
1. Implement `IWatcher` in `SmartRules.cs`:
   - `Start(RuleModel rule, CancellationToken ct)`
   - `Stop(string ruleId)`
   - emit `OnObservation` events with normalized `Observation` payload.
2. Keep payload minimal (hashes/tokens/scores), avoid raw captures by default.
3. Wire the watcher in `SmartRulesOrchestrator` constructor and subscribe to `OnObservation`.
4. Add any module setting/toggle to `AppSettings` + `MainForm` controls.
5. If needed, add rule-specific config in `RuleModel` and persist automatically via existing JSON rule storage.
