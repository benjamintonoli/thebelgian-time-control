using TheBelgian.TimeControl.Core.Payroll;
using TheBelgian.TimeControl.Core.Payroll.Models;
using TheBelgian.TimeControl.Infrastructure.Payroll.Normalization;

namespace TheBelgian.TimeControl.Infrastructure.Payroll.Sources;

public static class PayrollPerformanceMapper
{
    private const int TravelHfdTaakId = 5;
    private const int AbsenceHfdTaakIdLeave = 10;
    private const int AbsenceHfdTaakIdNzGe = 18;
    private const int StandbyHfdTaakId = 23;

    public static NormalizedPerformanceEntry Map(
        PlenionPayrollPerformanceRow row,
        PostcodeResolutionResult? postcodeResolution = null)
    {
        var postcode = postcodeResolution?.Postcode;
        var time = PerformanceTimeNormalizer.Normalize(row.Datum, row.Van, row.Tot);
        var pause = PauseNormalizer.Normalize(row.Pauze);
        var hfdTaakId = row.IdHfdTaak;

        return new NormalizedPerformanceEntry(
            SourceEntryId: row.IdProjPrest,
            SourceEntryKey: LegacySourceIdentity.ForPerformance(row.IdProjPrest),
            ResourceId: row.ResourceId,
            Date: row.Datum,
            Start: time.Start,
            End: time.End,
            AtlHoursRaw: row.AtlHoursRaw,
            AtlMinutesExact: PerformanceTimeNormalizer.AtlMinutesExact(row.AtlHoursRaw),
            GrossClockDuration: time.GrossClockDuration,
            Pause: pause,
            Km: row.Km,
            HfdTaakId: hfdTaakId,
            ProjectId: row.IdProj,
            ProjectNumber: row.ProjNr,
            BonNr: row.BonNr,
            Description: row.Omschr,
            Memo: row.Memo,
            Postcode: postcode,
            SortKey: row.IdProjPrest,
            IsTravel: hfdTaakId == TravelHfdTaakId,
            IsAbsence: hfdTaakId is AbsenceHfdTaakIdLeave or AbsenceHfdTaakIdNzGe,
            IsStandby: hfdTaakId == StandbyHfdTaakId,
            IsCalendarSynthetic: false);
    }

    public static IReadOnlyList<NormalizedPerformanceEntry> MapMany(
        IEnumerable<PlenionPayrollPerformanceRow> rows,
        IReadOnlyDictionary<long, PostcodeResolutionResult>? postcodesByPerformanceId = null)
    {
        return rows
            .Select(row =>
            {
                PostcodeResolutionResult? resolution = null;
                if (postcodesByPerformanceId is not null
                    && postcodesByPerformanceId.TryGetValue(row.IdProjPrest, out var resolved))
                {
                    resolution = resolved;
                }

                return Map(row, resolution);
            })
            .ToList();
    }
}
