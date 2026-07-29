using TheBelgian.TimeControl.Core.Models;

namespace TheBelgian.TimeControl.Infrastructure.Pilot;

/// <summary>
/// Evaluation scaffolding only. Does not compute final metrics while labels are missing.
/// Matching optimization must never load the locked holdout file.
/// </summary>
internal static class LocationMatchingBenchmarkEvaluation
{
    public static BenchmarkEvaluationScaffold Prepare(
        IReadOnlyList<LocationMatchingBenchmarkCase> cases) =>
        LocationMatchingBenchmarkSampling.BuildEvaluationScaffold(cases);

    public static BenchmarkLabeledMetrics? TryCompute(
        IReadOnlyList<LocationMatchingBenchmarkCase> cases,
        Func<LocationMatchingBenchmarkCase, string?> predictedStatus)
    {
        if (cases.Count == 0 || cases.Any(item => string.IsNullOrWhiteSpace(item.Label)))
        {
            return null;
        }

        var truePositive = 0;
        var falsePositive = 0;
        var falseNegative = 0;
        var trueNegative = 0;
        var covered = 0;
        foreach (var item in cases)
        {
            var predicted = predictedStatus(item);
            var reliablePredicted = IsReliablePrediction(predicted);
            var correct = IsCorrectLabel(item);
            if (reliablePredicted)
            {
                covered++;
            }

            if (reliablePredicted && correct)
            {
                truePositive++;
            }
            else if (reliablePredicted && !correct)
            {
                falsePositive++;
            }
            else if (!reliablePredicted && correct)
            {
                falseNegative++;
            }
            else
            {
                trueNegative++;
            }
        }

        var precision = Ratio(truePositive, truePositive + falsePositive);
        var recall = Ratio(truePositive, truePositive + falseNegative);
        var coverage = Ratio(covered, cases.Count);
        var f1 = precision + recall <= 0
            ? 0
            : 2 * precision * recall / (precision + recall);
        return new BenchmarkLabeledMetrics
        {
            CaseCount = cases.Count,
            TruePositives = truePositive,
            FalsePositives = falsePositive,
            FalseNegatives = falseNegative,
            TrueNegatives = trueNegative,
            Precision = Round(precision),
            Recall = Round(recall),
            Coverage = Round(coverage),
            F1 = Round(f1),
            Wilson95 = WilsonInterval(truePositive, truePositive + falsePositive),
            SeenLocation = Slice(cases, "SeenLocation", predictedStatus),
            UnseenLocation = Slice(cases, "UnseenLocation", predictedStatus),
        };
    }

    public static IReadOnlyList<(double Coverage, double Risk)> RiskCoverageCurve(
        IReadOnlyList<(double Confidence, bool Correct)> rankedPredictions)
    {
        if (rankedPredictions.Count == 0)
        {
            return [];
        }

        var ordered = rankedPredictions
            .OrderByDescending(item => item.Confidence)
            .ToArray();
        var points = new List<(double Coverage, double Risk)>(ordered.Length);
        var correct = 0;
        for (var index = 0; index < ordered.Length; index++)
        {
            if (ordered[index].Correct)
            {
                correct++;
            }

            var coverage = (index + 1d) / ordered.Length;
            var risk = 1d - (correct / (index + 1d));
            points.Add((Round(coverage), Round(risk)));
        }

        return points;
    }

    private static BenchmarkLabeledMetricsSlice? Slice(
        IReadOnlyList<LocationMatchingBenchmarkCase> cases,
        string exposure,
        Func<LocationMatchingBenchmarkCase, string?> predictedStatus)
    {
        var subset = cases
            .Where(item => string.Equals(item.LocationExposure, exposure, StringComparison.Ordinal))
            .ToArray();
        if (subset.Length == 0)
        {
            return new BenchmarkLabeledMetricsSlice
            {
                CaseCount = 0,
                Precision = 0,
                Recall = 0,
                Coverage = 0,
                F1 = 0,
            };
        }

        var metrics = TryCompute(subset, predictedStatus);
        return metrics is null
            ? null
            : new BenchmarkLabeledMetricsSlice
            {
                CaseCount = metrics.CaseCount,
                Precision = metrics.Precision,
                Recall = metrics.Recall,
                Coverage = metrics.Coverage,
                F1 = metrics.F1,
            };
    }

    private static bool IsReliablePrediction(string? status) =>
        status is "Confirmed" or "Probable" or "ConfirmedLocationMatch" or "ProbableLocationMatch";

    private static bool IsCorrectLabel(LocationMatchingBenchmarkCase item) =>
        string.Equals(item.Label, "CorrectCandidate", StringComparison.OrdinalIgnoreCase);

    private static double Ratio(int numerator, int denominator) =>
        denominator <= 0 ? 0 : (double)numerator / denominator;

    private static double Round(double value) =>
        Math.Round(value, 4);

    private static WilsonInterval WilsonInterval(int successes, int trials)
    {
        if (trials <= 0)
        {
            return new WilsonInterval { Lower = 0, Upper = 0 };
        }

        const double z = 1.959963984540054;
        var phat = successes / (double)trials;
        var denominator = 1 + (z * z / trials);
        var centre = phat + (z * z / (2 * trials));
        var margin = z * Math.Sqrt(((phat * (1 - phat)) + (z * z / (4 * trials))) / trials);
        return new WilsonInterval
        {
            Lower = Round(Math.Max(0, (centre - margin) / denominator)),
            Upper = Round(Math.Min(1, (centre + margin) / denominator)),
        };
    }
}
