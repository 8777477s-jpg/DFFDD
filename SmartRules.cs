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
    public int SignalStaleMs { get; set; } = 5000;
    public int MinActiveSignals { get; set; } = 1;
    public double RoiWeight { get; set; } = 0.70;
    public double UiaWeight { get; set; } = 0.20;
    public double OcrWeight { get; set; } = 0.10;
    public List<string> SuppressedOcrTokens { get; set; } = new();
}

public sealed class RuleRuntimeSignalState
{
    public Observation? LastRoi { get; set; }
    public Observation? LastUia { get; set; }
    public Observation? LastOcr { get; set; }
    public DateTime LastDecisionUtc { get; set; } = DateTime.MinValue;
    public int ConsecutiveAboveThreshold { get; set; }
    public int FalseFeedbackCount { get; set; }
    public int TrueFeedbackCount { get; set; }

    public Observation? LastByType(ObservationSourceType sourceType)
        => sourceType switch
        {
            ObservationSourceType.RoiDiff => LastRoi,
            ObservationSourceType.Uia => LastUia,
            ObservationSourceType.Ocr => LastOcr,
            _ => null
        };
}

public sealed class SmartRuleEvaluator
{
    public RuleEvaluationResult Evaluate(RuleModel rule, RuleRuntimeSignalState runtimeState)
    {
        var settings = rule.Smart;
        var nowUtc = DateTime.UtcNow;
        var factors = new List<(double contribution, string reason, bool active)>();

        AddFactor(factors, runtimeState.LastRoi, settings.RoiWeight, settings.SignalStaleMs, nowUtc);
        if (settings.EnableUiaWatcher) AddFactor(factors, runtimeState.LastUia, settings.UiaWeight, settings.SignalStaleMs, nowUtc);
        if (settings.EnableOcrWatcher) AddFactor(factors, runtimeState.LastOcr, settings.OcrWeight, settings.SignalStaleMs, nowUtc);

        int activeSignals = factors.Count(x => x.active);
        var score = Math.Clamp(factors.Sum(x => x.contribution), 0, 1);

        if (activeSignals < Math.Max(1, settings.MinActiveSignals))
            score *= 0.45;

        if (runtimeState.FalseFeedbackCount > runtimeState.TrueFeedbackCount)
            score *= 0.9;

        var topReasons = factors
            .OrderByDescending(x => x.contribution)
            .Take(3)
            .Select(x => x.reason)
            .ToList();

        if (activeSignals < Math.Max(1, settings.MinActiveSignals))
            topReasons.Add($"insufficient active signals ({activeSignals}/{Math.Max(1, settings.MinActiveSignals)})");

        var explanation = topReasons.Count == 0 ? "No sensor evidence yet." : string.Join(" | ", topReasons);
        bool aboveThreshold = score >= settings.FireThreshold;
        runtimeState.ConsecutiveAboveThreshold = aboveThreshold ? runtimeState.ConsecutiveAboveThreshold + 1 : 0;

        bool cooldownOver = (nowUtc - runtimeState.LastDecisionUtc).TotalMilliseconds >= settings.DecisionCooldownMs;
        bool stableEnough = runtimeState.ConsecutiveAboveThreshold >= Math.Max(1, settings.StabilityEvents);
        bool shouldFire = aboveThreshold && stableEnough && cooldownOver;

        if (shouldFire)
        {
            runtimeState.LastDecisionUtc = nowUtc;
            runtimeState.ConsecutiveAboveThreshold = 0;
        }

        return new RuleEvaluationResult
        {
            RuleScore = score,
            Explanation = explanation,
            ShouldFire = shouldFire
        };
    }

    private static void AddFactor(List<(double contribution, string reason, bool active)> factors, Observation? observation, double weight, int staleMs, DateTime nowUtc)
    {
        if (observation is null) return;

        var ageMs = Math.Max(0.0, (nowUtc - observation.TimestampUtc).TotalMilliseconds);
        var freshness = staleMs <= 0 ? 1.0 : Math.Clamp(1.0 - (ageMs / staleMs), 0.0, 1.0);
        var effectiveContribution = Math.Clamp(observation.Confidence * Math.Clamp(weight, 0.0, 1.0) * freshness, 0.0, 1.0);
        factors.Add((effectiveContribution, $"{observation.SourceType}: {observation.DebugReason} (freshness={freshness:0.00})", freshness > 0.05));
    }

    public static void ApplyFeedback(RuleModel rule, RuleRuntimeSignalState runtimeState, bool wasCorrect)
    {
        var smart = rule.Smart;
        if (wasCorrect)
        {
            runtimeState.TrueFeedbackCount += 1;
            smart.FireThreshold = Math.Clamp(smart.FireThreshold - 0.02, 0.35, 0.95);
            smart.DecisionCooldownMs = Math.Clamp(smart.DecisionCooldownMs - 50, 250, 5000);
            smart.RoiWeight += 0.02;
            smart.UiaWeight += 0.01;
            smart.OcrWeight += 0.01;
        }
        else
        {
            runtimeState.FalseFeedbackCount += 1;
            smart.FireThreshold = Math.Clamp(smart.FireThreshold + 0.04, 0.35, 0.98);
            smart.DecisionCooldownMs = Math.Clamp(smart.DecisionCooldownMs + 120, 250, 5000);
            smart.RoiWeight -= 0.03;
            smart.UiaWeight += 0.01;
            smart.OcrWeight += 0.02;

            var lastToken = runtimeState.LastOcr?.Payload.TryGetValue("token", out var token) == true ? token : null;
            if (!string.IsNullOrWhiteSpace(lastToken))
            {
                smart.SuppressedOcrTokens.Remove(lastToken);
                smart.SuppressedOcrTokens.Insert(0, lastToken);
                if (smart.SuppressedOcrTokens.Count > 20)
                    smart.SuppressedOcrTokens.RemoveRange(20, smart.SuppressedOcrTokens.Count - 20);
            }
        }

        NormalizeWeights(smart);
    }

    private static void NormalizeWeights(SmartRuleSettings smart)
    {
        smart.RoiWeight = Math.Clamp(smart.RoiWeight, 0.05, 0.90);
        smart.UiaWeight = Math.Clamp(smart.UiaWeight, 0.05, 0.80);
        smart.OcrWeight = Math.Clamp(smart.OcrWeight, 0.05, 0.80);

        var total = smart.RoiWeight + smart.UiaWeight + smart.OcrWeight;
        if (total <= 0.0001)
        {
            smart.RoiWeight = 0.7;
            smart.UiaWeight = 0.2;
            smart.OcrWeight = 0.1;
            return;
        }

        smart.RoiWeight /= total;
        smart.UiaWeight /= total;
        smart.OcrWeight /= total;
    }
}

public sealed class OcrTextMatcher
{
    public static double MatchConfidence(string observedText, SmartRuleSettings settings)
    {
        if (string.IsNullOrWhiteSpace(observedText)) return 0.0;

        var value = settings.OcrTrim ? observedText.Trim() : observedText;
        if (settings.OcrCaseInsensitive) value = value.ToLowerInvariant();

        if (settings.SuppressedOcrTokens.Any(x => string.Equals(x, value, StringComparison.Ordinal)))
            return 0.0;

        if (string.IsNullOrWhiteSpace(settings.OcrPattern))
            return 0.4;

        var pattern = settings.OcrTrim ? settings.OcrPattern.Trim() : settings.OcrPattern;
        if (settings.OcrCaseInsensitive) pattern = pattern.ToLowerInvariant();

        if (settings.OcrPatternIsRegex)
        {
            try { return Regex.IsMatch(value, pattern) ? 1.0 : 0.0; }
            catch { return 0.0; }
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
