# Smart Rules Architecture (Stage 1)

## Components
- **Watchers (`IWatcher`)**: `RoiDiffWatcher`, `UiaWatcher`, `OcrWatcher` emit normalized `Observation` records.
- **SmartRuleEngine**: collects bounded observation buffers, computes rule score and explanation, applies decision policy (threshold + stability + cooldown), stores score history.
- **Controller**: deterministic lifecycle manager (arm/monitor/fire/run/resume) and safety owner (lease, panic, cancel).
- **Storage**: persists rules + bounded `rule_scores` diagnostics history.

## Threads and execution model
- ROI watch loop continues in existing monitor worker tasks.
- UIA watcher runs all subscription and polling work on a **single dedicated MTA thread** using a serial queue to avoid cross-thread handler churn.
- OCR watcher runs on cancellable background tasks at configured sampling interval.
- Macro execution remains deterministic in existing playback flow.

## Rule lifecycle
1. User arms a rule.
2. Enabled watchers start and emit observations.
3. On ROI trigger candidate, engine evaluates combined observations -> score + explanation.
4. Decision policy requires threshold and stability ticks and honors cooldown.
5. If fire approved, controller acquires lease and runs macro deterministically.
6. Rule re-enters monitoring (or disarms based on repeat policy).

## Data flow
`Observation -> SmartRuleEngine.Evaluate -> RuleScoreSnapshot -> DecisionPolicy -> Controller Action`

- Observation payloads are compact by default (tokens, scores, metadata).
- Raw image capture is not persisted in Stage 1 default path.
- Timeline includes score + top reasons for explainability.

## Privacy and safety
- OCR/UIA modules are opt-in with explicit settings toggles.
- Monitoring scope remains explicit (rule ROI/window scope).
- Default storage keeps compact text/scores only.
- Panic and cancel paths still release input lease and disarm active rules.
