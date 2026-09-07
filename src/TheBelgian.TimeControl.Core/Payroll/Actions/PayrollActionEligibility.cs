using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Core.Payroll.Findings;

namespace TheBelgian.TimeControl.Core.Payroll.Actions;

/// <summary>
/// Pure eligibility rules for human-approved payroll actions.
/// Detection engines remain unchanged; this layer only interprets findings.
/// </summary>
public static class PayrollActionEligibility
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly HashSet<PayrollFindingType> SpecialProjectReviewOnly =
    [
        PayrollFindingType.Project300WithoutPlanning,
        PayrollFindingType.Project200WithoutPlanning,
        PayrollFindingType.Project200ExceedsPlanning,
        PayrollFindingType.Project100TrainingHours,
        PayrollFindingType.Project100TrainingInOvertime,
        PayrollFindingType.Project100ExceedsPlannedDuration,
    ];

    public static PayrollIntervalSemantics ClassifyMissingTechnicianInterval(PayrollFindingRecord finding)
    {
        if (finding.FindingType != PayrollFindingType.MissingPlannedTechnicianPerformance)
        {
            return PayrollIntervalSemantics.Ambiguous;
        }

        // GPS-derived High proposals use trip min/max (travel/arrival evidence), not proven payable work.
        // Ayrton 2026-08-31: 08:35–10:06 = Dendermonde→Aalst→Leuven site ARRIVAL, not booked work.
        if (string.Equals(
                finding.GpsClassification,
                nameof(MissingTechnicianEvidenceClass.PlanningPlusPeerPlusGps),
                StringComparison.Ordinal))
        {
            return PayrollIntervalSemantics.GpsTravelOnly;
        }

        if (string.Equals(
                finding.GpsClassification,
                nameof(MissingTechnicianEvidenceClass.Ambiguous),
                StringComparison.Ordinal)
            || string.Equals(
                finding.GpsClassification,
                nameof(MissingTechnicianEvidenceClass.NoGpsData),
                StringComparison.Ordinal)
            || string.Equals(
                finding.GpsClassification,
                nameof(MissingTechnicianEvidenceClass.PlanningPlusPeer),
                StringComparison.Ordinal)
            || string.Equals(
                finding.GpsClassification,
                nameof(MissingTechnicianEvidenceClass.PlanningPlusGps),
                StringComparison.Ordinal))
        {
            return PayrollIntervalSemantics.Ambiguous;
        }

        return PayrollIntervalSemantics.Ambiguous;
    }

    public static PayrollActionEligibilityResult Evaluate(
        PayrollFindingRecord finding,
        PayrollActionEligibilityContext context)
    {
        var evidence = BuildEvidenceSnapshot(finding);
        return finding.FindingType switch
        {
            PayrollFindingType.MissingPlannedTechnicianPerformance =>
                EvaluateCreate(finding, evidence, context),
            PayrollFindingType.StandbyStartMismatch or PayrollFindingType.StandbyEndMismatch =>
                EvaluateAdjust(finding, evidence, context),
            _ => BlockUnsupported(finding, evidence),
        };
    }

    public static string ComputeSourceRevision(
        PayrollActionEvidenceSnapshot evidence,
        PayrollActionCreateProposal? create,
        PayrollActionAdjustProposal? adjust)
    {
        var payload = JsonSerializer.Serialize(new
        {
            evidence,
            create,
            adjust,
        }, JsonOptions);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash);
    }

    private static PayrollActionEligibilityResult EvaluateCreate(
        PayrollFindingRecord finding,
        PayrollActionEvidenceSnapshot evidence,
        PayrollActionEligibilityContext context)
    {
        const PayrollProposedActionType actionType = PayrollProposedActionType.CreateMissingPerformance;
        var semantics = context.IntervalSemanticsOverride
            ?? ClassifyMissingTechnicianInterval(finding);

        if (!context.IsEmployeeIncluded)
        {
            return Block(actionType, evidence, semantics, PayrollActionBlockReasonCode.ExcludedEmployee,
                "Medewerker is uitgesloten van payroll; geen create-voorstel.");
        }

        if (context.IsMonthFinalized)
        {
            return Block(actionType, evidence, semantics, PayrollActionBlockReasonCode.MonthFinalized,
                "Payrollmaand is afgesloten; geen create-voorstel.");
        }

        if (IsNoGps(finding))
        {
            return Block(actionType, evidence, PayrollIntervalSemantics.Ambiguous,
                PayrollActionBlockReasonCode.NoGpsData,
                "Geen bruikbare GPS-data; create is niet uitvoerbaar.");
        }

        if (IsAmbiguousOrContradicted(finding))
        {
            return Block(actionType, evidence, PayrollIntervalSemantics.Ambiguous,
                PayrollActionBlockReasonCode.AmbiguousInterval,
                "GPS/bewijs is onduidelijk of tegenstrijdig; create is niet uitvoerbaar.");
        }

        if (string.Equals(
                finding.GpsClassification,
                nameof(MissingTechnicianEvidenceClass.ContradictedByExistingPerformance),
                StringComparison.Ordinal))
        {
            return Block(actionType, evidence, PayrollIntervalSemantics.Ambiguous,
                PayrollActionBlockReasonCode.ConflictingPerformance,
                "Conflicterende prestatie in hetzelfde venster; create is geblokkeerd.");
        }

        if (finding.Severity != PayrollFindingSeverity.High)
        {
            return Block(actionType, evidence, semantics, PayrollActionBlockReasonCode.SeverityInsufficient,
                "Alleen High-bevindingen kunnen een create-voorstel worden.");
        }

        if (context.HasMatchingExistingPerformance)
        {
            return Block(actionType, evidence, semantics, PayrollActionBlockReasonCode.MatchingPerformanceExists,
                "Er bestaat al een matching prestatie; create is niet nodig.");
        }

        if (semantics == PayrollIntervalSemantics.GpsTravelOnly)
        {
            return Block(actionType, evidence, semantics, PayrollActionBlockReasonCode.GpsTravelOnlyInterval,
                BuildGpsTravelOnlyDutchReason(finding));
        }

        if (semantics != PayrollIntervalSemantics.PayableWork)
        {
            return Block(actionType, evidence, semantics, PayrollActionBlockReasonCode.AmbiguousInterval,
                "Interval is niet bewezen als betaalbare arbeidstijd; create is geblokkeerd.");
        }

        if (finding.SuggestedPayableStart is null
            || finding.SuggestedPayableEnd is null
            || finding.SuggestedPayableEnd <= finding.SuggestedPayableStart
            || string.IsNullOrWhiteSpace(finding.SuggestedProjectId)
            || string.IsNullOrWhiteSpace(finding.ResourceId))
        {
            return Block(actionType, evidence, semantics, PayrollActionBlockReasonCode.IncompleteTarget,
                "Create-target is onvolledig (resource/datum/project/VAN/TOT ontbreken).");
        }

        // Never invent or copy peer IDHFDTAAK — only an independently proven MainTaskId unlocks Ready.
        if (context.ProvenMainTaskId is null or <= 0)
        {
            return Block(actionType, evidence, semantics, PayrollActionBlockReasonCode.MissingMainTaskId,
                "IDHFDTAAK/taaktype is niet veilig afleidbaar. Peer-IDHFDTAAK wordt niet gekopieerd; create is geblokkeerd.");
        }

        var hours = finding.SuggestedPayableHours
            ?? (decimal)(finding.SuggestedPayableEnd.Value - finding.SuggestedPayableStart.Value).TotalHours;
        if (hours <= 0m)
        {
            return Block(actionType, evidence, semantics, PayrollActionBlockReasonCode.IncompleteTarget,
                "Create-interval heeft geen positieve duur.");
        }

        var proposal = new PayrollActionCreateProposal(
            finding.ResourceId,
            finding.Date,
            finding.SuggestedPayableStart.Value,
            finding.SuggestedPayableEnd.Value,
            Math.Round(hours, 2, MidpointRounding.AwayFromZero),
            finding.SuggestedProjectId.Trim(),
            string.IsNullOrWhiteSpace(finding.SuggestedBonNr) ? null : finding.SuggestedBonNr.Trim(),
            context.ProvenMainTaskId.Value,
            semantics);

        var revision = ComputeSourceRevision(evidence, proposal, null);
        return new PayrollActionEligibilityResult(
            actionType,
            PayrollProposedActionStatus.ReadyForApproval,
            semantics,
            PayrollActionBlockReasonCode.None,
            null,
            proposal,
            null,
            evidence,
            revision);
    }

    private static PayrollActionEligibilityResult EvaluateAdjust(
        PayrollFindingRecord finding,
        PayrollActionEvidenceSnapshot evidence,
        PayrollActionEligibilityContext context)
    {
        const PayrollProposedActionType actionType = PayrollProposedActionType.AdjustExistingPerformanceTime;

        if (!context.IsEmployeeIncluded)
        {
            return Block(actionType, evidence, PayrollIntervalSemantics.PayableWork,
                PayrollActionBlockReasonCode.ExcludedEmployee,
                "Medewerker is uitgesloten van payroll; geen correctievoorstel.");
        }

        if (context.IsMonthFinalized)
        {
            return Block(actionType, evidence, PayrollIntervalSemantics.PayableWork,
                PayrollActionBlockReasonCode.MonthFinalized,
                "Payrollmaand is afgesloten; geen correctievoorstel.");
        }

        if (finding.Severity != PayrollFindingSeverity.High)
        {
            return Block(actionType, evidence, PayrollIntervalSemantics.PayableWork,
                PayrollActionBlockReasonCode.SeverityInsufficient,
                "Alleen betrouwbare High wachtdienst start/eind-mismatches kunnen een correctievoorstel worden.");
        }

        if (!string.Equals(
                finding.GpsClassification,
                nameof(StandbyGpsClassification.PhysicalIntervention),
                StringComparison.Ordinal))
        {
            return Block(actionType, evidence, PayrollIntervalSemantics.Ambiguous,
                PayrollActionBlockReasonCode.AmbiguousStandbyEvidence,
                "Wachtdienst-GPS is niet betrouwbaar genoeg voor een tijdsuggestie.");
        }

        var related = ParseRelatedIds(finding.RelatedPerformanceIdsJson);
        if (related.Count != 1
            || context.ExistingPerformanceId is null
            || related[0] != context.ExistingPerformanceId.Value)
        {
            return Block(actionType, evidence, PayrollIntervalSemantics.PayableWork,
                PayrollActionBlockReasonCode.RelatedPerformanceAmbiguous,
                "Correctie vereist precies één gerelateerde prestatie met bekende huidige VAN/TOT.");
        }

        if (finding.SuggestedPayableStart is null
            || finding.SuggestedPayableEnd is null
            || context.ExistingPerformanceStart is null
            || context.ExistingPerformanceEnd is null)
        {
            return Block(actionType, evidence, PayrollIntervalSemantics.PayableWork,
                PayrollActionBlockReasonCode.IncompleteTarget,
                "Correctievereisten ontbreken (huidige of voorgestelde VAN/TOT).");
        }

        var proposal = new PayrollActionAdjustProposal(
            context.ExistingPerformanceId.Value,
            context.ExistingPerformanceStart.Value,
            context.ExistingPerformanceEnd.Value,
            finding.SuggestedPayableStart.Value,
            finding.SuggestedPayableEnd.Value,
            context.ExistingActivityType,
            context.ExistingMainTaskExternalId);

        var revision = ComputeSourceRevision(evidence, null, proposal);
        return new PayrollActionEligibilityResult(
            actionType,
            PayrollProposedActionStatus.ReadyForApproval,
            PayrollIntervalSemantics.PayableWork,
            PayrollActionBlockReasonCode.None,
            null,
            null,
            proposal,
            evidence,
            revision);
    }

    private static PayrollActionEligibilityResult BlockUnsupported(
        PayrollFindingRecord finding,
        PayrollActionEvidenceSnapshot evidence)
    {
        if (SpecialProjectReviewOnly.Contains(finding.FindingType))
        {
            return Block(
                InferDefaultType(finding),
                evidence,
                PayrollIntervalSemantics.Ambiguous,
                PayrollActionBlockReasonCode.UnsupportedFindingType,
                "Project 100/200/300-bevindingen zijn review-only; geen uitvoerbare actie in 9A.");
        }

        if (finding.FindingType == PayrollFindingType.OverlappingPerformances)
        {
            return Block(
                PayrollProposedActionType.AdjustExistingPerformanceTime,
                evidence,
                PayrollIntervalSemantics.Ambiguous,
                PayrollActionBlockReasonCode.OverlapWithoutDeterministicTarget,
                "Overlap zonder deterministisch doelprestatie; geen correctievoorstel.");
        }

        if (finding.FindingType == PayrollFindingType.StandbyPossibleWrongDossier)
        {
            return Block(
                PayrollProposedActionType.AdjustExistingPerformanceTime,
                evidence,
                PayrollIntervalSemantics.Ambiguous,
                PayrollActionBlockReasonCode.WrongDossierReviewOnly,
                "Mogelijk verkeerd dossier blijft review-only; geen dossiercorrectie in 9A.");
        }

        if (finding.FindingType is PayrollFindingType.StandbyAmbiguousEvidence
            or PayrollFindingType.StandbyNoGpsData
            or PayrollFindingType.StandbyPhoneExceeds15Min
            or PayrollFindingType.StandbyDurationMismatch)
        {
            return Block(
                PayrollProposedActionType.AdjustExistingPerformanceTime,
                evidence,
                PayrollIntervalSemantics.Ambiguous,
                PayrollActionBlockReasonCode.AmbiguousStandbyEvidence,
                "Wachtdienstbevinding is niet geschikt voor een betrouwbare tijdsuggestie.");
        }

        return Block(
            InferDefaultType(finding),
            evidence,
            PayrollIntervalSemantics.Ambiguous,
            PayrollActionBlockReasonCode.UnsupportedFindingType,
            "Deze bevinding ondersteunt geen uitvoerbare payrollactie.");
    }

    private static PayrollProposedActionType InferDefaultType(PayrollFindingRecord finding) =>
        finding.FindingType == PayrollFindingType.MissingPlannedTechnicianPerformance
            ? PayrollProposedActionType.CreateMissingPerformance
            : PayrollProposedActionType.AdjustExistingPerformanceTime;

    private static PayrollActionEligibilityResult Block(
        PayrollProposedActionType actionType,
        PayrollActionEvidenceSnapshot evidence,
        PayrollIntervalSemantics semantics,
        PayrollActionBlockReasonCode code,
        string reason)
    {
        var revision = ComputeSourceRevision(evidence, null, null);
        return new PayrollActionEligibilityResult(
            actionType,
            PayrollProposedActionStatus.Blocked,
            semantics,
            code,
            reason,
            null,
            null,
            evidence,
            revision);
    }

    private static string BuildGpsTravelOnlyDutchReason(PayrollFindingRecord finding)
    {
        var start = finding.SuggestedPayableStart?.ToString("HH:mm", CultureInfo.GetCultureInfo("nl-BE")) ?? "—";
        var end = finding.SuggestedPayableEnd?.ToString("HH:mm", CultureInfo.GetCultureInfo("nl-BE")) ?? "—";
        var hours = finding.SuggestedPayableHours?.ToString("0.##", CultureInfo.GetCultureInfo("nl-BE")) ?? "—";
        return
            $"Voorstel niet uitvoerbaar: GPS-interval {start}–{end} ({hours} u) is reis-naar-werf-bewijs "
            + "(vertrek/aankomst), geen bewezen betaalbare arbeidstijd. "
            + "Peer-IDHFDTAAK mag niet worden gekopieerd. Geen create zonder bewezen payable work + taaktype.";
    }

    private static bool IsNoGps(PayrollFindingRecord finding) =>
        string.Equals(finding.GpsClassification, nameof(MissingTechnicianEvidenceClass.NoGpsData), StringComparison.Ordinal)
        || string.Equals(finding.GpsClassification, nameof(StandbyGpsClassification.NoGpsData), StringComparison.Ordinal);

    private static bool IsAmbiguousOrContradicted(PayrollFindingRecord finding) =>
        string.Equals(finding.GpsClassification, nameof(MissingTechnicianEvidenceClass.Ambiguous), StringComparison.Ordinal)
        || string.Equals(finding.GpsClassification, nameof(MissingTechnicianEvidenceClass.ContradictedByGps), StringComparison.Ordinal)
        || string.Equals(finding.GpsClassification, nameof(StandbyGpsClassification.Ambiguous), StringComparison.Ordinal);

    private static PayrollActionEvidenceSnapshot BuildEvidenceSnapshot(PayrollFindingRecord finding) =>
        new(
            finding.FindingKey,
            finding.FindingType,
            finding.Severity,
            finding.GpsClassification,
            finding.Evidence,
            finding.Title,
            finding.Description,
            ParseRelatedIds(finding.RelatedPerformanceIdsJson),
            finding.SuggestedPayableStart,
            finding.SuggestedPayableEnd,
            finding.SuggestedPayableHours,
            finding.SuggestedProjectId,
            finding.SuggestedBonNr);

    public static IReadOnlyList<long> ParseRelatedIds(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<long[]>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string DefaultComment(PayrollProposedActionType actionType) =>
        actionType switch
        {
            PayrollProposedActionType.CreateMissingPerformance =>
                "TimeControl payrollcontrole — ontbrekende prestatie",
            PayrollProposedActionType.AdjustExistingPerformanceTime =>
                "TimeControl payrollcontrole — correctie wachtdienst",
            _ => "TimeControl payrollcontrole",
        };

    public static string UiStatusLabel(PayrollProposedActionStatus status) =>
        status switch
        {
            PayrollProposedActionStatus.ReadyForApproval => "Voorstel klaar",
            PayrollProposedActionStatus.Applied => "Reeds uitgevoerd",
            PayrollProposedActionStatus.Blocked => "Voorstel niet uitvoerbaar",
            PayrollProposedActionStatus.Stale => "Voorstel verouderd",
            PayrollProposedActionStatus.Failed => "Uitvoering mislukt",
            PayrollProposedActionStatus.Executing => "Bezig met uitvoeren",
            PayrollProposedActionStatus.Cancelled => "Geannuleerd",
            _ => "Voorstel",
        };
}
