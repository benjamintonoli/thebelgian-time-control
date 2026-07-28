using TheBelgian.TimeControl.Core.Configuration;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;

namespace TheBelgian.TimeControl.Core.Services;

public sealed class TimeControlMatchingService : ITimeControlMatchingService
{
    private readonly MatchingOptions _options;
    private readonly TimeProvider _timeProvider;

    public TimeControlMatchingService(
        MatchingOptions options,
        TimeProvider? timeProvider = null)
    {
        options.Validate();
        _options = options;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public IReadOnlyList<DetectedException> Detect(
        DailyTechnicianTimeline timeline,
        IReadOnlyCollection<DetectedException>? history = null)
    {
        if (!timeline.HasCertainVehicleAssignment)
        {
            return
            [
                Create(
                    timeline,
                    ExceptionType.UncertainVehicleAssignment,
                    0,
                    "Het gekoppelde voertuig wijkt af of kon niet zeker worden toegewezen.")
            ];
        }

        if (timeline.PlenionStart is null || timeline.PlenionEnd is null ||
            timeline.FirstTripStart is null || timeline.LastTripEnd is null)
        {
            return
            [
                Create(
                    timeline,
                    ExceptionType.InsufficientPowerfleetData,
                    0,
                    "Begin of einde ontbreekt in Plenion of Powerfleet.")
            ];
        }

        var startDifference = WholeMinutes(timeline.FirstTripStart.Value - timeline.PlenionStart.Value);
        var endDifference = WholeMinutes(timeline.PlenionEnd.Value - timeline.LastTripEnd.Value);
        var travelDifference = timeline.RegisteredTravelMinutes - timeline.DrivingMinutes;
        var detected = new List<DetectedException>();

        AddIndividual(
            detected,
            timeline,
            ExceptionType.RegisteredStartTooEarly,
            startDifference,
            "De geregistreerde start ligt vóór de eerste Powerfleet-rit.",
            startDifference,
            endDifference,
            travelDifference);
        AddIndividual(
            detected,
            timeline,
            ExceptionType.RegisteredEndTooLate,
            endDifference,
            "De geregistreerde eindtijd ligt na de laatste Powerfleet-rit.",
            startDifference,
            endDifference,
            travelDifference);
        AddIndividual(
            detected,
            timeline,
            ExceptionType.RegisteredTravelExceedsPowerfleet,
            travelDifference,
            "De geregistreerde verplaatsing is hoger dan de Powerfleet-rijtijd.",
            startDifference,
            endDifference,
            travelDifference);

        var dailyBeneficialDifference = new[] { startDifference, endDifference, travelDifference }
            .Where(value => value >= _options.PatternDifferenceMinutes)
            .DefaultIfEmpty(0)
            .Max();

        if (dailyBeneficialDifference > 0 &&
            IsStructuralPattern(timeline, dailyBeneficialDifference, history ?? []))
        {
            detected.Add(Create(
                timeline,
                ExceptionType.StructuralPattern,
                dailyBeneficialDifference,
                "De afwijking voldoet aan de configureerbare patroonregels.",
                startDifference,
                endDifference,
                travelDifference));
        }

        if (detected.Count == 0)
        {
            detected.Add(Create(
                timeline,
                ExceptionType.None,
                0,
                "Verschillen vallen binnen de configureerbare tolerantie.",
                startDifference,
                endDifference,
                travelDifference));
        }

        return detected;
    }

    private void AddIndividual(
        List<DetectedException> target,
        DailyTechnicianTimeline timeline,
        ExceptionType type,
        int difference,
        string reason,
        int startDifference,
        int endDifference,
        int travelDifference)
    {
        if (difference < _options.IndividualExceptionMinutes)
        {
            return;
        }

        target.Add(Create(
            timeline,
            type,
            difference,
            reason,
            startDifference,
            endDifference,
            travelDifference));
    }

    private bool IsStructuralPattern(
        DailyTechnicianTimeline timeline,
        int currentDifference,
        IReadOnlyCollection<DetectedException> history)
    {
        var priorDailyDifferences = history
            .Where(item =>
                item.TechnicianExternalId == timeline.TechnicianExternalId &&
                item.Date < timeline.Date &&
                item.DifferenceMinutes >= _options.PatternDifferenceMinutes &&
                item.Type is ExceptionType.RegisteredStartTooEarly
                    or ExceptionType.RegisteredEndTooLate
                    or ExceptionType.RegisteredTravelExceedsPowerfleet)
            .GroupBy(item => item.Date)
            .Select(group => new
            {
                Date = group.Key,
                Minutes = group.Max(item => item.DifferenceMinutes),
            })
            .OrderByDescending(item => item.Date)
            .Take(_options.PatternWindowDays - 1)
            .Select(item => item.Minutes)
            .Append(currentDifference)
            .ToArray();

        return priorDailyDifferences.Length >= _options.PatternMinimumOccurrences &&
               priorDailyDifferences.Sum() >= _options.PatternCumulativeMinutes;
    }

    private DetectedException Create(
        DailyTechnicianTimeline timeline,
        ExceptionType type,
        int difference,
        string reason,
        int startDifference = 0,
        int endDifference = 0,
        int travelDifference = 0)
    {
        var now = _timeProvider.GetUtcNow();
        return new DetectedException
        {
            ExternalKey = $"{timeline.TechnicianExternalId}:{timeline.Date:yyyyMMdd}:{type}",
            TechnicianExternalId = timeline.TechnicianExternalId,
            TechnicianName = timeline.TechnicianName,
            Date = timeline.Date,
            Type = type,
            DifferenceMinutes = difference,
            Priority = difference >= _options.HighPriorityExceptionMinutes
                ? ExceptionPriority.High
                : difference >= _options.IndividualExceptionMinutes
                    ? ExceptionPriority.Normal
                    : ExceptionPriority.Low,
            Reason = reason,
            ReviewDecision = ReviewDecision.Unreviewed,
            PlenionStart = timeline.PlenionStart,
            PlenionEnd = timeline.PlenionEnd,
            RegisteredMinutes = timeline.RegisteredMinutes,
            BreakMinutes = timeline.BreakMinutes,
            FirstTripStart = timeline.FirstTripStart,
            LastTripEnd = timeline.LastTripEnd,
            DrivingMinutes = timeline.DrivingMinutes,
            PowerfleetDistanceKilometres = timeline.PowerfleetDistanceKilometres,
            StartDifferenceMinutes = startDifference,
            EndDifferenceMinutes = endDifference,
            TravelDifferenceMinutes = travelDifference,
            IgnoreToleranceMinutes = _options.IgnoreDifferenceMinutes,
            IndividualToleranceMinutes = _options.IndividualExceptionMinutes,
            HighPriorityToleranceMinutes = _options.HighPriorityExceptionMinutes,
            CreatedAt = now,
            LastCalculatedAt = now,
        };
    }

    private static int WholeMinutes(TimeSpan value) =>
        (int)Math.Round(value.TotalMinutes, MidpointRounding.AwayFromZero);
}
