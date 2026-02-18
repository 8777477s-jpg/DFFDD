using System.Collections.Concurrent;

namespace BoltMacro;

public enum ObservationSourceType
{
    RoiDiff = 0,
    Uia = 1,
    Ocr = 2,
    WindowState = 3
}

public enum TriggerKind
{
    RoiDiff = 0,
    UiaText = 1,
    OcrText = 2
}

public sealed class Observation
{
    public string RuleId { get; init; } = "";
    public ObservationSourceType SourceType { get; init; }
    public string Scope { get; init; } = "";
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;
    public Dictionary<string, string> Payload { get; init; } = new();
    public double Confidence { get; init; }
    public string DebugReason { get; init; } = "";
}

public sealed class RuleScoreSnapshot
{
    public string RuleId { get; init; } = "";
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;
    public double Score { get; init; }
    public string Explanation { get; init; } = "";
    public bool DecisionFire { get; init; }
}

public interface IWatcher : IDisposable
{
    event Action<Observation>? OnObservation;
    void Start(RuleModel rule, CancellationToken externalToken);
    void Stop(string ruleId);
    bool IsRunning(string ruleId);
}

public sealed class RuleEvaluator
{
    public RuleScoreSnapshot Evaluate(RuleModel rule, IReadOnlyCollection<Observation> observations)
    {
        var latest = observations
            .GroupBy(x => x.SourceType)
            .Select(g => g.OrderByDescending(x => x.TimestampUtc).First())
            .ToList();

        double roiWeight = Math.Max(0.05, rule.Smart.RoiWeight);
        double uiaWeight = Math.Max(0.0, rule.Smart.UiaWeight);
        double ocrWeight = Math.Max(0.0, rule.Smart.OcrWeight);
        double totalWeight = roiWeight + uiaWeight + ocrWeight;
        if (totalWeight <= 0.001) totalWeight = 1.0;

        double weighted = 0.0;
        var reasons = new List<(double Impact, string Text)>();

        foreach (var obs in latest)
        {
            double w = obs.SourceType switch
            {
                ObservationSourceType.RoiDiff => roiWeight,
                ObservationSourceType.Uia => uiaWeight,
                ObservationSourceType.Ocr => ocrWeight,
                _ => 0.0
            };

            if (w <= 0) continue;
            weighted += obs.Confidence * w;
            reasons.Add((obs.Confidence * w, $"{obs.SourceType}: {obs.DebugReason} ({obs.Confidence:0.00})"));
        }

        double score = Math.Clamp((weighted / totalWeight) + rule.Smart.AdaptiveBias, 0.0, 1.0);
        var explanation = string.Join(" | ", reasons.OrderByDescending(x => x.Impact).Take(3).Select(x => x.Text));
        if (string.IsNullOrWhiteSpace(explanation)) explanation = "No active sensor evidence";

        return new RuleScoreSnapshot
        {
            RuleId = rule.Id,
            TimestampUtc = DateTime.UtcNow,
            Score = score,
            Explanation = explanation,
            DecisionFire = score >= rule.Smart.FireThreshold
        };
    }
}

public sealed class DecisionPolicyState
{
    public int ConsecutivePasses { get; set; }
    public DateTime CooldownUntilUtc { get; set; } = DateTime.MinValue;
}

public sealed class SmartRuleEngine
{
    private readonly RuleEvaluator _evaluator = new();
    private readonly Storage _storage;
    private readonly TimelineService _timeline;

    private readonly ConcurrentDictionary<string, List<Observation>> _obs = new();
    private readonly ConcurrentDictionary<string, RuleScoreSnapshot> _latest = new();
    private readonly ConcurrentDictionary<string, DecisionPolicyState> _policy = new();

    public SmartRuleEngine(Storage storage, TimelineService timeline)
    {
        _storage = storage;
        _timeline = timeline;
    }

    public void RegisterWatcher(IWatcher watcher)
    {
        watcher.OnObservation += o => AcceptObservation(o);
    }

    public RuleScoreSnapshot? GetLatestScore(string ruleId)
        => _latest.TryGetValue(ruleId, out var s) ? s : null;

    public void AcceptObservation(Observation obs)
    {
        var list = _obs.GetOrAdd(obs.RuleId, _ => new List<Observation>());
        lock (list)
        {
            list.Add(obs);
            if (list.Count > 120) list.RemoveRange(0, list.Count - 120);
        }
    }

    public RuleScoreSnapshot Evaluate(RuleModel rule)
    {
        var list = _obs.GetOrAdd(rule.Id, _ => new List<Observation>());
        List<Observation> copy;
        lock (list) copy = list.ToList();

        var snapshot = _evaluator.Evaluate(rule, copy);
        _latest[rule.Id] = snapshot;
        _storage.InsertRuleScore(snapshot);
        return snapshot;
    }

    public bool ShouldFire(RuleModel rule, RuleScoreSnapshot snapshot)
    {
        var st = _policy.GetOrAdd(rule.Id, _ => new DecisionPolicyState());

        if (DateTime.UtcNow < st.CooldownUntilUtc) return false;

        if (snapshot.DecisionFire) st.ConsecutivePasses++;
        else st.ConsecutivePasses = 0;

        if (st.ConsecutivePasses < Math.Max(1, rule.Smart.StabilityTicks)) return false;

        st.ConsecutivePasses = 0;
        st.CooldownUntilUtc = DateTime.UtcNow.AddMilliseconds(Math.Max(0, rule.Trigger.CooldownMs));
        _timeline.Add(new TimelineEvent { Source = TimelineSource.Trigger, Message = $"[SmartRule] score={snapshot.Score:0.000} fire=true reason={snapshot.Explanation}" });
        return true;
    }

    public void ApplyFeedback(RuleModel rule, bool correct)
    {
        if (correct)
        {
            rule.Smart.AdaptiveBias = Math.Clamp(rule.Smart.AdaptiveBias + 0.02, -0.2, 0.2);
            rule.Smart.FireThreshold = Math.Clamp(rule.Smart.FireThreshold - 0.01, 0.2, 0.95);
        }
        else
        {
            rule.Smart.AdaptiveBias = Math.Clamp(rule.Smart.AdaptiveBias - 0.03, -0.2, 0.2);
            rule.Smart.FireThreshold = Math.Clamp(rule.Smart.FireThreshold + 0.02, 0.2, 0.95);
        }
    }
}
