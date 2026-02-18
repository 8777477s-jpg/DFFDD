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
        var settings = rule.Smart;
        var reasons = new List<(double score, string reason)>();

        if (runtimeState.LastRoi is not null)
        {
            var roiContribution = Math.Clamp(runtimeState.LastRoi.Confidence * settings.RoiWeight, 0, 1);
            reasons.Add((roiContribution, runtimeState.LastRoi.DebugReason));
        }

        if (settings.EnableUiaWatcher && runtimeState.LastUia is not null)
        {
            var uiaContribution = Math.Clamp(runtimeState.LastUia.Confidence * settings.UiaWeight, 0, 1);
            reasons.Add((uiaContribution, runtimeState.LastUia.DebugReason));
        }

        if (settings.EnableOcrWatcher && runtimeState.LastOcr is not null)
        {
            var ocrContribution = Math.Clamp(runtimeState.LastOcr.Confidence * settings.OcrWeight, 0, 1);
            reasons.Add((ocrContribution, runtimeState.LastOcr.DebugReason));
        }

        var score = Math.Clamp(reasons.Sum(x => x.score), 0, 1);
        var topReasons = reasons.OrderByDescending(x => x.score).Take(3).Select(x => x.reason).ToArray();
        var explanation = topReasons.Length == 0 ? "No sensor evidence yet." : string.Join(" | ", topReasons);

        bool aboveThreshold = score >= settings.FireThreshold;
        runtimeState.ConsecutiveAboveThreshold = aboveThreshold ? runtimeState.ConsecutiveAboveThreshold + 1 : 0;

        bool cooldownOver = (DateTime.UtcNow - runtimeState.LastDecisionUtc).TotalMilliseconds >= settings.DecisionCooldownMs;
        bool shouldFire = aboveThreshold && runtimeState.ConsecutiveAboveThreshold >= Math.Max(1, settings.StabilityEvents) && cooldownOver;
        if (shouldFire)
        {
            runtimeState.LastDecisionUtc = DateTime.UtcNow;
            runtimeState.ConsecutiveAboveThreshold = 0;
        }

        return new RuleEvaluationResult
        {
            RuleScore = score,
            Explanation = explanation,
            ShouldFire = shouldFire
        };
    }

    public static void ApplyFeedback(RuleModel rule, bool wasCorrect)
    {
        var smart = rule.Smart;
        if (wasCorrect)
        {
            smart.FireThreshold = Math.Clamp(smart.FireThreshold - 0.02, 0.35, 0.95);
            smart.RoiWeight = Math.Clamp(smart.RoiWeight + 0.03, 0.40, 0.90);
        }
        else
        {
            smart.FireThreshold = Math.Clamp(smart.FireThreshold + 0.04, 0.35, 0.98);
            smart.RoiWeight = Math.Clamp(smart.RoiWeight - 0.03, 0.20, 0.90);
            smart.DecisionCooldownMs = Math.Clamp(smart.DecisionCooldownMs + 100, 400, 5000);
        }
    }
}

public sealed class OcrTextMatcher
{
    public static double MatchConfidence(string observedText, SmartRuleSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.OcrPattern))
            return 0.4;

        var value = settings.OcrTrim ? observedText.Trim() : observedText;
        var pattern = settings.OcrTrim ? settings.OcrPattern.Trim() : settings.OcrPattern;

        if (settings.OcrCaseInsensitive)
        {
            value = value.ToLowerInvariant();
            pattern = pattern.ToLowerInvariant();
        }

        if (settings.OcrPatternIsRegex)
            return Regex.IsMatch(value, pattern) ? 1.0 : 0.0;

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
