using TheBelgian.TimeControl.Core.Configuration;

namespace TheBelgian.TimeControl.Core.Models;

public sealed record VisitCandidate(
    string VisitId,
    DateOnly Date,
    DateTimeOffset FirstArrival,
    DateTimeOffset LastDeparture,
    int TotalDwellMinutes,
    double? CenterLatitude,
    double? CenterLongitude,
    double RadiusMeters,
    IReadOnlyList<string> ConstituentStopIds,
    IReadOnlyList<string> Addresses,
    IReadOnlyList<PilotStop> ConstituentStops)
{
    public int OverlapMinutes(DateTimeOffset performanceStart, DateTimeOffset performanceEnd)
    {
        var start = FirstArrival > performanceStart ? FirstArrival : performanceStart;
        var end = LastDeparture < performanceEnd ? LastDeparture : performanceEnd;
        return end <= start
            ? 0
            : (int)Math.Round((end - start).TotalMinutes, MidpointRounding.AwayFromZero);
    }

    public MergedPilotStop ToMergedPilotStop(AdaptiveLocationMatchingOptions options) =>
        new(
            VisitId,
            Date,
            FirstArrival,
            LastDeparture,
            TotalDwellMinutes,
            Addresses.FirstOrDefault(address => !string.IsNullOrWhiteSpace(address)),
            CenterLatitude is null ? null : (decimal)CenterLatitude.Value,
            CenterLongitude is null ? null : (decimal)CenterLongitude.Value,
            ConstituentStops.Count > 0 ? ConstituentStops[0].DriverId : null,
            ConstituentStops.Count > 0 ? ConstituentStops[0].DriverName : null,
            ConstituentStopIds,
            TotalDwellMinutes < options.PassThroughMaxDurationMinutes);
}
