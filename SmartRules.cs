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

public enum RuleFeedbackKind
{
    CorrectTrigger = 0,
    FalseTrigger = 1
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
    public DateTime UtcTime { get; init; } = DateTime.UtcNow;
    public double Score { get; init; }
    public string Explanation { get; init; } = "";
}

public sealed class RuleEvaluationResult
{
    public double Score { get; init; }
    public string Explanation { get; init; } = "";
    public List<string> TopReasons { get; init; } = new();
}

public interface IWatcher : IDisposable
{
    event Action<Observation>? OnObservation;
    void Start(RuleModel rule, CancellationToken externalCt);
    void Stop(string ruleId);
    bool IsRunning(string ruleId);
}

public sealed class RuleEvaluator
{
    public RuleEvaluationResult Evaluate(RuleModel rule, Observation triggerObservation, IReadOnlyDictionary<ObservationSourceType, Observation> latest)
    {
        var reasons = new List<(double weight, string reason)>();

        double roiWeight = Math.Clamp(rule.Trigger.RoiWeight, 0.1, 1.0);
        double uiaWeight = rule.Trigger.UseUiaWatcher ? Math.Clamp(rule.Trigger.UiaWeight, 0.0, 1.0) : 0;
        double ocrWeight = rule.Trigger.UseOcrWatcher ? Math.Clamp(rule.Trigger.OcrWeight, 0.0, 1.0) : 0;

        double weighted = triggerObservation.Confidence * roiWeight;
        double total = roiWeight;
        reasons.Add((triggerObservation.Confidence * roiWeight, $"ROI confidence={triggerObservation.Confidence:0.00} ({triggerObservation.DebugReason})"));

        if (uiaWeight > 0 && latest.TryGetValue(ObservationSourceType.Uia, out var uiaObs))
        {
            var age = (DateTime.UtcNow - uiaObs.TimestampUtc).TotalSeconds;
            double freshness = age <= 8 ? 1.0 : 0.4;
            double contribution = uiaObs.Confidence * uiaWeight * freshness;
            weighted += contribution;
            total += uiaWeight;
            reasons.Add((contribution, $"UIA confidence={uiaObs.Confidence:0.00}, age={age:0.0}s ({uiaObs.DebugReason})"));
        }

        if (ocrWeight > 0 && latest.TryGetValue(ObservationSourceType.Ocr, out var ocrObs))
        {
            var age = (DateTime.UtcNow - ocrObs.TimestampUtc).TotalSeconds;
            double freshness = age <= 8 ? 1.0 : 0.4;
            double contribution = ocrObs.Confidence * ocrWeight * freshness;
            weighted += contribution;
            total += ocrWeight;
            reasons.Add((contribution, $"OCR confidence={ocrObs.Confidence:0.00}, age={age:0.0}s ({ocrObs.DebugReason})"));
        }

        double score = total <= 0 ? 0 : Math.Clamp(weighted / total, 0.0, 1.0);
        var top = reasons.OrderByDescending(x => x.weight).Take(3).Select(x => x.reason).ToList();

        return new RuleEvaluationResult
        {
            Score = score,
            TopReasons = top,
            Explanation = top.Count == 0 ? "No contributing observations." : string.Join(" | ", top)
        };
    }

    public static double MatchTextConfidence(string candidate, string? expected, string mode)
    {
        if (string.IsNullOrWhiteSpace(expected)) return 0.5;
        var a = (candidate ?? string.Empty).Trim();
        var b = expected.Trim();

        if (mode.Equals("equals", StringComparison.OrdinalIgnoreCase))
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.0;

        if (mode.Equals("regex", StringComparison.OrdinalIgnoreCase))
        {
            try { return Regex.IsMatch(a, b, RegexOptions.IgnoreCase) ? 1.0 : 0.0; }
            catch { return 0.0; }
        }

        return a.Contains(b, StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.0;
    }
}
