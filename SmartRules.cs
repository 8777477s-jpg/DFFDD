using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace BoltMacro;

public enum ObservationSourceType
{
    RoiDiff = 0,
    Uia = 1,
    Ocr = 2,
    WindowState = 3
}

public enum WatcherType
{
    RoiDiff = 0,
    UiaText = 1,
    OcrText = 2
}

public sealed class Observation
{
    public ObservationSourceType SourceType { get; init; }
    public string Scope { get; init; } = "";
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;
    public Dictionary<string, string> Payload { get; init; } = new();
    public double Confidence { get; init; }
    public string DebugReason { get; init; } = "";
}

public interface IWatcher : IDisposable
{
    event Action<Observation>? OnObservation;
    void Start(RuleModel rule, CancellationToken externalCt);
    void Stop(string ruleId);
    bool IsRunning(string ruleId);
}

public sealed class RuleEvaluationResult
{
    public double RuleScore { get; init; }
    public string Explanation { get; init; } = "";
    public bool ShouldFire { get; init; }
}

public sealed class RuleScoreSample
{
    public string Id { get; set; } = IdUtil.NewId();
    public string RuleId { get; set; } = "";
    public DateTime UtcTime { get; set; } = DateTime.UtcNow;
    public double Score { get; set; }
    public string Explanation { get; set; } = "";
    public string TopReasons { get; set; } = "";
}

public sealed class RuleFeedbackRecord
{
    public string Id { get; set; } = IdUtil.NewId();
    public string RuleId { get; set; } = "";
    public DateTime UtcTime { get; set; } = DateTime.UtcNow;
    public bool WasCorrect { get; set; }
    public string? Note { get; set; }
}

public sealed class SmartRuleSettings
{
    public bool EnableSmartRules { get; set; } = true;
    public bool EnableUiaWatcher { get; set; } = false;
    public bool EnableOcrWatcher { get; set; } = false;
    public string UiaSelectorRecipe { get; set; } = "";
    public string OcrPattern { get; set; } = "";
    public bool OcrPatternIsRegex { get; set; } = false;
    public bool OcrCaseInsensitive { get; set; } = true;
    public bool OcrTrim { get; set; } = true;
    public double FireThreshold { get; set; } = 0.55;
    public int StabilityEvents { get; set; } = 1;
    public int DecisionCooldownMs { get; set; } = 1000;
    public double RoiWeight { get; set; } = 0.70;
    public double UiaWeight { get; set; } = 0.20;
    public double OcrWeight { get; set; } = 0.10;
    public int SignalMaxAgeMs { get; set; } = 1800;
}

public sealed class RuleRuntimeSignalState
{
    public Observation? LastRoi { get; set; }
    public Observation? LastUia { get; set; }
    public Observation? LastOcr { get; set; }
    public DateTime LastDecisionUtc { get; set; } = DateTime.MinValue;
    public int ConsecutiveAboveThreshold { get; set; }
}

public sealed class SmartRuleEvaluator
{
    public RuleEvaluationResult Evaluate(RuleModel rule, RuleRuntimeSignalState runtimeState)
    {
        var now = DateTime.UtcNow;
        var settings = rule.Smart;
        var maxAgeMs = Math.Max(250, settings.SignalMaxAgeMs);

        var factors = new List<(double weightedScore, double weight, string reason)>();
        AddFactor(factors, runtimeState.LastRoi, settings.RoiWeight, maxAgeMs, now);

        if (settings.EnableUiaWatcher)
            AddFactor(factors, runtimeState.LastUia, settings.UiaWeight, maxAgeMs, now);

        if (settings.EnableOcrWatcher)
            AddFactor(factors, runtimeState.LastOcr, settings.OcrWeight, maxAgeMs, now);

        var weightSum = factors.Sum(x => x.weight);
        var weightedSum = factors.Sum(x => x.weightedScore);
        var score = weightSum > 0.0001 ? Math.Clamp(weightedSum / weightSum, 0, 1) : 0;

        var topReasons = factors
            .OrderByDescending(x => x.weightedScore)
            .Take(3)
            .Select(x => x.reason)
            .ToArray();

        var explanation = topReasons.Length == 0 ? "No fresh sensor evidence yet." : string.Join(" | ", topReasons);

        bool aboveThreshold = score >= settings.FireThreshold;
        runtimeState.ConsecutiveAboveThreshold = aboveThreshold
            ? runtimeState.ConsecutiveAboveThreshold + 1
            : 0;

        bool cooldownOver = (now - runtimeState.LastDecisionUtc).TotalMilliseconds >= settings.DecisionCooldownMs;
        bool shouldFire = aboveThreshold &&
                          runtimeState.ConsecutiveAboveThreshold >= Math.Max(1, settings.StabilityEvents) &&
                          cooldownOver;

        if (shouldFire)
        {
            runtimeState.LastDecisionUtc = now;
            runtimeState.ConsecutiveAboveThreshold = 0;
        }

        return new RuleEvaluationResult
        {
            RuleScore = score,
            Explanation = explanation,
            ShouldFire = shouldFire
        };
    }

    private static void AddFactor(
        ICollection<(double weightedScore, double weight, string reason)> factors,
        Observation? obs,
        double configuredWeight,
        int maxAgeMs,
        DateTime now)
    {
        if (obs is null || configuredWeight <= 0) return;

        var ageMs = Math.Max(0, (now - obs.TimestampUtc).TotalMilliseconds);
        var freshness = Math.Clamp(1.0 - (ageMs / maxAgeMs), 0, 1);
        if (freshness <= 0.01) return;

        var score = Math.Clamp(obs.Confidence, 0, 1) * freshness;
        var reason = $"{obs.SourceType}: {obs.DebugReason} (fresh={freshness:0.00})";
        factors.Add((score * configuredWeight, configuredWeight, reason));
    }

    public static void ApplyFeedback(RuleModel rule, bool wasCorrect)
    {
        var smart = rule.Smart;
        if (wasCorrect)
        {
            smart.FireThreshold = Math.Clamp(smart.FireThreshold - 0.02, 0.30, 0.95);
            smart.RoiWeight = Math.Clamp(smart.RoiWeight + 0.02, 0.35, 0.90);
            smart.DecisionCooldownMs = Math.Clamp(smart.DecisionCooldownMs - 50, 250, 5000);
        }
        else
        {
            smart.FireThreshold = Math.Clamp(smart.FireThreshold + 0.04, 0.35, 0.98);
            smart.RoiWeight = Math.Clamp(smart.RoiWeight - 0.03, 0.20, 0.90);
            smart.DecisionCooldownMs = Math.Clamp(smart.DecisionCooldownMs + 120, 300, 5000);
            smart.StabilityEvents = Math.Clamp(smart.StabilityEvents + 1, 1, 8);
        }
    }
}

public sealed class OcrTextMatcher
{
    public static double MatchConfidence(string observedText, SmartRuleSettings settings)
    {
        if (string.IsNullOrWhiteSpace(observedText))
            return 0.0;

        if (string.IsNullOrWhiteSpace(settings.OcrPattern))
            return 0.45;

        var value = settings.OcrTrim ? observedText.Trim() : observedText;
        var pattern = settings.OcrTrim ? settings.OcrPattern.Trim() : settings.OcrPattern;

        if (settings.OcrCaseInsensitive)
        {
            value = value.ToLowerInvariant();
            pattern = pattern.ToLowerInvariant();
        }

        try
        {
            if (settings.OcrPatternIsRegex)
                return Regex.IsMatch(value, pattern) ? 1.0 : 0.0;
        }
        catch
        {
            return 0.0;
        }

        if (value.Equals(pattern, StringComparison.Ordinal)) return 1.0;
        if (value.Contains(pattern, StringComparison.Ordinal)) return 0.85;
        return 0.0;
    }
}

public sealed class RuleRuntimeCache
{
    private readonly ConcurrentDictionary<string, RuleRuntimeSignalState> _cache = new();

    public RuleRuntimeSignalState ForRule(string ruleId) => _cache.GetOrAdd(ruleId, _ => new RuleRuntimeSignalState());
    public void Remove(string ruleId) => _cache.TryRemove(ruleId, out _);
}
