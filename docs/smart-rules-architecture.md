# Smart Rules Architecture (Stage 1, improved)

## Components

- **WatcherCoordinator**: manages pluggable sensors (`RoiDiffWatcher`, `UiaWatcher`, `OcrWatcher`) and forwards unified observations.
- **Observation model**: normalized payload (`SourceType`, `Scope`, timestamp, confidence, debug reason, compact payload).
- **SmartRuleEvaluator**: computes weighted score with freshness decay, stability checks, and cooldown.
- **Controller**: deterministic action layer (lease + panic + cancel) unchanged; smartness only affects detection/decision.
- **Storage**: bounded score history + feedback persistence.

## Threads and synchronization

- **UI thread**: WinForms only.
- **ROI watcher**: existing async monitor worker.
- **UIA watcher**: dedicated **MTA thread** with serialized queue for Start/Stop and per-rule polling loop.
- **OCR watcher**: per-rule background loop with fail-soft exception handling.

All sensor failures are logged and do not crash runtime.

## Rule lifecycle

1. Rule armed in controller.
2. Coordinator starts ROI watcher (always) and optional UIA/OCR watchers.
3. Watchers emit `Observation` events with rule scope.
4. Controller updates per-rule runtime cache and evaluates score.
5. If score is stable above threshold and cooldown passed, deterministic macro run executes.
6. Rule returns to monitoring or disarms by repeat policy.

Compatibility mode: when Smart Rules are disabled for a rule, ROI observation immediately triggers (MVP behavior preserved).

## Data flow

`Watcher -> Observation -> RuntimeSignalState -> SmartRuleEvaluator -> (score + explanation) -> DecisionPolicy -> HandleRuleFireAsync`

Persisted diagnostics:

- Timeline entries with score/top reasons.
- `rule_score_history` (pruned ring-style, max 120 per rule).
- `rule_feedback` rows for correct/false-trigger learning.

## Privacy/local-first defaults

- Monitoring starts only for explicitly armed rules with explicit ROIs.
- OCR watcher stores compact visual token + confidence (no raw screenshot retention).
- No network calls.
- Diagnostics remain local.
