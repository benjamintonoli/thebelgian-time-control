using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Infrastructure.Pilot;

namespace TheBelgian.TimeControl.Tests;

public sealed class LocationMatchingCalibrationBatchServiceTests
{
    [Fact]
    public void ValidateLabelFile_RejectsInvalidExpectedStopId()
    {
        var calibration = new[]
        {
            Case(10, candidateStopIds: ["A", "B"]),
        };
        var labels = new[]
        {
            new CalibrationLabelEntry
            {
                PerformanceId = 10,
                Label = "CorrectCandidate",
                ExpectedStopId = "MISSING",
                ReviewerConfidence = "High",
                ReviewerNote = null,
            },
        };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            LocationMatchingCalibrationBatchService.ValidateLabelFile(labels, calibration));
        Assert.Contains("bestaat niet", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateLabelFile_AcceptsCompleteValidFile()
    {
        var calibration = Enumerable.Range(1, 30)
            .Select(id => Case(id, candidateStopIds: id % 2 == 0 ? ["S1"] : []))
            .ToArray();
        var labels = calibration.Select(item =>
        {
            var hasCandidate = item.Candidates.Count > 0;
            return new CalibrationLabelEntry
            {
                PerformanceId = item.PerformanceId,
                Label = hasCandidate ? "CorrectCandidate" : "NoValidCandidate",
                ExpectedStopId = hasCandidate ? item.Candidates[0].StopId : null,
                ReviewerConfidence = "Medium",
                ReviewerNote = null,
            };
        }).ToArray();

        LocationMatchingCalibrationBatchService.ValidateLabelFile(labels, calibration);
    }

    [Fact]
    public void ExportReviewPack_WritesThirtyBlindCasesWithoutScores()
    {
        var temp = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "calib-export-" + Guid.NewGuid().ToString("N")));
        try
        {
            var cases = Enumerable.Range(1, 30)
                .Select(id => Case(id, candidateStopIds: ["Z-stop", "A-stop"]))
                .Select(item => item with
                {
                    IsCalibrationCase = true,
                    RequiresSecondReview = true,
                    ExistingMatchStatus = "ConfirmedLocationMatch",
                })
                .ToArray();
            File.WriteAllText(
                Path.Combine(temp.FullName, LocationMatchingBenchmarkService.CalibrationFileName),
                System.Text.Json.JsonSerializer.Serialize(
                    new LocationMatchingCalibrationFile
                    {
                        DatasetRole = "calibration",
                        RandomSeed = 1,
                        Cases = cases,
                        Agreement = LocationMatchingBenchmarkSampling.ComputeLabelAgreement(cases),
                    }));

            var exported = LocationMatchingCalibrationBatchService.ExportReviewPack(temp.FullName);
            Assert.Equal(30, exported.CaseCount);
            Assert.True(File.Exists(exported.MarkdownPath));
            Assert.True(File.Exists(exported.JsonPath));
            Assert.True(File.Exists(exported.TemplatePath));

            var pack = System.Text.Json.JsonSerializer.Deserialize<CalibrationReviewPack>(
                File.ReadAllText(exported.JsonPath));
            Assert.NotNull(pack);
            Assert.Equal(30, pack!.CaseCount);
            Assert.All(pack.Cases, item =>
            {
                Assert.DoesNotContain(
                    "ConfirmedLocationMatch",
                    System.Text.Json.JsonSerializer.Serialize(item),
                    StringComparison.Ordinal);
                Assert.Equal(
                    ["A-stop", "Z-stop"],
                    item.Candidates.Select(candidate => candidate.StopId).ToArray());
            });

            var template = System.Text.Json.JsonSerializer.Deserialize<List<CalibrationLabelEntry>>(
                File.ReadAllText(exported.TemplatePath));
            Assert.NotNull(template);
            Assert.Equal(30, template!.Count);
            Assert.All(template, item =>
            {
                Assert.Null(item.Label);
                Assert.Null(item.ExpectedStopId);
                Assert.Null(item.ReviewerConfidence);
                Assert.Null(item.ReviewerNote);
            });
        }
        finally
        {
            Directory.Delete(temp.FullName, recursive: true);
        }
    }

    private static LocationMatchingBenchmarkCase Case(
        long id,
        string[]? candidateStopIds = null)
    {
        var date = new DateOnly(2026, 6, 1);
        var start = new DateTimeOffset(date.ToDateTime(new TimeOnly(9, 0), DateTimeKind.Unspecified), TimeSpan.FromHours(2));
        var end = new DateTimeOffset(date.ToDateTime(new TimeOnly(11, 0), DateTimeKind.Unspecified), TimeSpan.FromHours(2));
        var candidates = (candidateStopIds ?? ["S1"])
            .Select((stopId, index) => new LocationMatchingBenchmarkCandidate
            {
                StopId = stopId,
                Address = $"Addr {stopId}",
                DistanceMeters = 50 + index,
                Arrival = start, // same arrival → order by StopId (not score)
                Departure = end,
                OverlapMinutes = 60,
                StartDifferenceMinutes = 100 - index, // score-like noise must not drive order
                EndDifferenceMinutes = index,
                ExistingCandidateStatus = "ConfirmedLocationMatch",
                ExistingCandidateScore = 100 - index,
                Explanation = "test",
            })
            .ToArray();
        return new LocationMatchingBenchmarkCase
        {
            PerformanceId = id,
            Technician = "Filip Dekuyper",
            Date = date,
            Start = start,
            End = end,
            Lacleunik = $"L{id}",
            PlenionAddress = "Test 1",
            GeocodeQuality = GeocodeQualityClass.PartialAddress,
            ExistingMatchStatus = "ConfirmedLocationMatch",
            ActivityType = "CustomerWork",
            LocationExposure = "SeenLocation",
            Candidates = candidates,
            IsCalibrationCase = true,
            RequiresSecondReview = true,
        };
    }
}
