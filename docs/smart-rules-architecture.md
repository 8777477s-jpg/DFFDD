# Smart Rules Architecture (Stage 1)

## Components

- **WatcherCoordinator**: orchestrates independent sensor plugins per rule (`RoiDiffWatcher`, `UiaWatcher`, `OcrWatcher`).
- **Observation model**: unified envelope for sensor outputs (`SourceType`, `Scope`, timestamp, payload, confidence, debug reason).
- **SmartRuleEvaluator**: deterministic score combiner and decision policy (threshold + stability + cooldown).
- **Controller runtime loop**: keeps existing deterministic action execution while delegating detection to score/decision.
- **Storage**: persists bounded score history and feedback in SQLite.

## Threads and concurrency

- **Main UI thread** remains WinForms-only.
- **ROI watcher** keeps existing async monitoring worker from `RoiMonitorService`.
- **UIA watcher** uses a dedicated **MTA thread** with a serialized action queue for handler add/remove order.
- **OCR watcher** uses a per-rule background task at configurable interval.
- Sensor failures are fail-soft: each watcher catches/logs exceptions without crashing engine.

## Rule lifecycle

1. User arms a rule.
2. Controller starts watcher plugins via `WatcherCoordinator`.
3. Watchers emit `Observation` events.
4. Controller updates per-rule runtime signal cache.
5. `SmartRuleEvaluator` computes score + explanation and applies policy.
6. If decision is fire: existing deterministic macro runner executes with lease/panic/cancel safety.
7. After run, rule resumes monitoring or disarms via existing repeat policy.

If Smart Rules are toggled off for a rule, ROI observation fires immediately (MVP-compatible behavior).

## Data flow

`Watcher -> Observation -> RuleRuntimeSignalState -> SmartRuleEvaluator -> RuleScore/Explanation -> Decision -> Controller.HandleRuleFireAsync -> PlaybackService`

Persisted side effects:

- Timeline event with score + top reason summary (includes freshness/active-signal reasoning).
- `rule_score_history` row (bounded to recent 120 samples per rule).
- Optional `rule_feedback` row when user marks correct/false trigger.

## Privacy/local-first defaults

- Sensors run only for armed rules with explicit ROI/context.
- OCR watcher stores compact token/confidence metadata only by default.
- No network calls are introduced.
- Raw captures are not persisted in Stage 1 default path.


## Stage-1 smartness upgrades in this revision

- Fixed watcher event fan-out leak by subscribing watcher events once in coordinator and routing by parsed scope.
- Added stale-signal decay to prevent old observations from indefinitely inflating score.
- Added minimum active-signal gate before firing (configurable per rule).
- Added safe false-positive suppression list for OCR tokens via feedback loop.
