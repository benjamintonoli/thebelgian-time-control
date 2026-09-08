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
                EvaluateAdjust([finding], evidence, context),
            _ => BlockUnsupported(finding, evidence),
        };
    }

    /// <summary>
    /// Evaluates one aggregated ADJUST action for all Start/End mismatch findings on the same performance.
    /// </summary>
    public static PayrollActionEligibilityResult EvaluateStandbyAdjustGroup(
        IReadOnlyList<PayrollFindingRecord> findings,
        PayrollActionEligibilityContext context)
    {
        if (findings.Count == 0)
        {
            throw new ArgumentException("At least one standby mismatch finding is required.", nameof(findings));
        }

        if (findings.Any(item =>
                item.FindingType is not (PayrollFindingType.StandbyStartMismatch
                    or PayrollFindingType.StandbyEndMismatch)))
        {
            throw new ArgumentException("Only StandbyStart/EndMismatch findings may be aggregated.", nameof(findings));
        }

        var primary = findings
            .OrderBy(item => item.FindingType)
            .ThenBy(item => item.FindingKey, StringComparer.Ordinal)
            .First();
        var evidence = BuildAggregatedEvidenceSnapshot(findings, primary);
        return EvaluateAdjust(findings, evidence, context);
    }


    public static string ComputeSourceRevision(
        PayrollActionEvidenceSnapshot evidence,
        PayrollActionCreateProposal? create,
        PayrollActionAdjustProposal? adjust,
        PayrollActionDeleteProposal? delete = null)
    {
        var payload = JsonSerializer.Serialize(new
        {
            evidence,
            create,
            adjust,
            delete,
        }, JsonOptions);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash);
    }

    /// <summary>
    /// Workbench / snapshotted adjust-delete actions validate live Plenion against the stored proposal
    /// instead of re-running SpecialProjectReviewOnly eligibility (which would mark them Stale).
    /// Standby adjust keys keep the EvaluateStandbyAdjustGroup path.
    /// </summary>
    public static bool IsWorkbenchOrSnapshottedAction(
        string? findingKey,
        PayrollProposedActionType actionType,
        PayrollActionAdjustProposal? adjust = null)
    {
        if (actionType == PayrollProposedActionType.DeleteExistingPerformance)
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(findingKey)
            && findingKey.StartsWith("standby-adjust:", StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(findingKey)
            && (findingKey.StartsWith("p300-adjust:", StringComparison.Ordinal)
                || findingKey.StartsWith("p300-delete:", StringComparison.Ordinal)
                || findingKey.StartsWith("p200-adjust:", StringComparison.Ordinal)
                || findingKey.StartsWith("p200-delete:", StringComparison.Ordinal)))
        {
            return true;
        }

        return actionType == PayrollProposedActionType.AdjustExistingPerformanceTime
            && adjust is not null
            && PayrollPwsSupportedActivities.IsSupported(adjust.ExpectedActivityType);
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
        IReadOnlyList<PayrollFindingRecord> findings,
        PayrollActionEvidenceSnapshot evidence,
        PayrollActionEligibilityContext context)
    {
        const PayrollProposedActionType actionType = PayrollProposedActionType.AdjustExistingPerformanceTime;
        var primary = findings[0];

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

        if (findings.Any(item => item.Severity != PayrollFindingSeverity.High))
        {
            return Block(actionType, evidence, PayrollIntervalSemantics.PayableWork,
                PayrollActionBlockReasonCode.SeverityInsufficient,
                "Alleen betrouwbare High wachtdienst start/eind-mismatches kunnen een correctievoorstel worden.");
        }

        if (findings.Any(IsNoGps)
            || findings.Any(item => string.Equals(
                item.GpsClassification,
                nameof(StandbyGpsClassification.NoGpsData),
                StringComparison.Ordinal)))
        {
            return Block(actionType, evidence, PayrollIntervalSemantics.Ambiguous,
                PayrollActionBlockReasonCode.NoGpsData,
                "Geen bruikbare GPS-data; wachtdienstcorrectie is geblokkeerd.");
        }

        if (findings.Any(item => !string.Equals(
                item.GpsClassification,
                nameof(StandbyGpsClassification.PhysicalIntervention),
                StringComparison.Ordinal)))
        {
            return Block(actionType, evidence, PayrollIntervalSemantics.Ambiguous,
                PayrollActionBlockReasonCode.AmbiguousStandbyEvidence,
                "Wachtdienst-GPS is niet betrouwbaar genoeg voor een tijdsuggestie.");
        }

        if (findings.Any(IsAmbiguousOrContradicted))
        {
            return Block(actionType, evidence, PayrollIntervalSemantics.Ambiguous,
                PayrollActionBlockReasonCode.AmbiguousStandbyEvidence,
                "Wachtdienst-GPS is ambigu; correctie is geblokkeerd.");
        }

        if (context.HasRelatedDossierAmbiguity)
        {
            return Block(actionType, evidence, PayrollIntervalSemantics.Ambiguous,
                PayrollActionBlockReasonCode.ConflictingDossierAmbiguity,
                "Tijdgerelateerde dossier-ambiguïteit; correctie is geblokkeerd.");
        }

        if (context.HasConflictingPerformance)
        {
            return Block(actionType, evidence, PayrollIntervalSemantics.PayableWork,
                PayrollActionBlockReasonCode.ConflictingPerformance,
                "Conflicterende prestatie overlapping het voorgestelde callout-venster.");
        }

        var related = findings
            .SelectMany(item => ParseRelatedIds(item.RelatedPerformanceIdsJson))
            .Distinct()
            .ToList();
        if (related.Count != 1
            || context.ExistingPerformanceId is null
            || related[0] != context.ExistingPerformanceId.Value)
        {
            return Block(actionType, evidence, PayrollIntervalSemantics.PayableWork,
                PayrollActionBlockReasonCode.RelatedPerformanceAmbiguous,
                "Correctie vereist precies één gerelateerde prestatie met bekende huidige VAN/TOT.");
        }

        var activityType = context.ExistingActivityType
            ?? PayrollStandbyActivityTypes.FromMainTaskExternalId(context.ExistingMainTaskExternalId);
        if (!PayrollStandbyActivityTypes.IsWaitingTimePerformance(
                context.ExistingMainTaskExternalId,
                activityType))
        {
            return Block(actionType, evidence, PayrollIntervalSemantics.PayableWork,
                PayrollActionBlockReasonCode.WrongActivityType,
                "Wachtdienstcorrectie vereist IDHFDTAAK=23 en activity WaitingTime; geen fake mapping toegestaan.");
        }

        var proposedStart = findings
            .Select(item => item.SuggestedPayableStart)
            .Where(item => item.HasValue)
            .Select(item => item!.Value)
            .DefaultIfEmpty()
            .Min();
        var proposedEnd = findings
            .Select(item => item.SuggestedPayableEnd)
            .Where(item => item.HasValue)
            .Select(item => item!.Value)
            .DefaultIfEmpty()
            .Max();

        if (proposedStart == default
            || proposedEnd == default
            || context.ExistingPerformanceStart is null
            || context.ExistingPerformanceEnd is null)
        {
            return Block(actionType, evidence, PayrollIntervalSemantics.PayableWork,
                PayrollActionBlockReasonCode.IncompleteTarget,
                "Correctievereisten ontbreken (huidige of voorgestelde VAN/TOT).");
        }

        if (proposedEnd <= proposedStart)
        {
            return Block(actionType, evidence, PayrollIntervalSemantics.PayableWork,
                PayrollActionBlockReasonCode.NonPositiveDuration,
                "Voorstelduur is niet positief; correctie is geblokkeerd.");
        }

        var callout = context.CalloutAssessmentOverride
            ?? StandbyCalloutEvidence.Assess(
                proposedStart,
                proposedEnd,
                context.ExistingPerformanceStart.Value,
                context.ExistingPerformanceEnd.Value,
                context.StandbyDayTrips ?? []);

        if (!callout.IsComplete)
        {
            var blockedProposal = new PayrollActionAdjustProposal(
                context.ExistingPerformanceId.Value,
                context.ExistingPerformanceStart.Value,
                context.ExistingPerformanceEnd.Value,
                proposedStart,
                proposedEnd,
                PayrollStandbyActivityTypes.WaitingTime,
                PayrollStandbyActivityTypes.WaitingMainTaskExternalId);
            var blockedEvidence = evidence with
            {
                CalloutEvidence = callout.EvidenceNote,
                Evidence = string.IsNullOrWhiteSpace(evidence.Evidence)
                    ? callout.EvidenceNote
                    : evidence.Evidence + " | " + callout.EvidenceNote,
                SuggestedPayableStart = proposedStart,
                SuggestedPayableEnd = proposedEnd,
            };
            var blockedRevision = ComputeSourceRevision(blockedEvidence, null, blockedProposal);
            return new PayrollActionEligibilityResult(
                actionType,
                PayrollProposedActionStatus.Blocked,
                PayrollIntervalSemantics.Ambiguous,
                callout.BlockReasonCode,
                callout.BlockReason ?? "Incomplete fysieke callout; correctie is geblokkeerd.",
                null,
                blockedProposal,
                blockedEvidence,
                blockedRevision);
        }

        var proposal = new PayrollActionAdjustProposal(
            context.ExistingPerformanceId.Value,
            context.ExistingPerformanceStart.Value,
            context.ExistingPerformanceEnd.Value,
            proposedStart,
            proposedEnd,
            PayrollStandbyActivityTypes.WaitingTime,
            PayrollStandbyActivityTypes.WaitingMainTaskExternalId);

        var enrichedEvidence = evidence with
        {
            CalloutEvidence = callout.EvidenceNote,
            Evidence = string.IsNullOrWhiteSpace(evidence.Evidence)
                ? callout.EvidenceNote
                : evidence.Evidence + " | " + callout.EvidenceNote,
            SuggestedPayableStart = proposedStart,
            SuggestedPayableEnd = proposedEnd,
            SuggestedPayableHours = Math.Round(
                (decimal)(proposedEnd - proposedStart).TotalHours,
                2,
                MidpointRounding.AwayFromZero),
        };

        var revision = ComputeSourceRevision(enrichedEvidence, null, proposal);
        return new PayrollActionEligibilityResult(
            actionType,
            PayrollProposedActionStatus.ReadyForApproval,
            PayrollIntervalSemantics.PayableWork,
            PayrollActionBlockReasonCode.None,
            null,
            null,
            proposal,
            enrichedEvidence,
            revision);
    }

    private static PayrollActionEvidenceSnapshot BuildAggregatedEvidenceSnapshot(
        IReadOnlyList<PayrollFindingRecord> findings,
        PayrollFindingRecord primary)
    {
        var keys = findings
            .Select(item => item.FindingKey)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();
        var ids = findings
            .Where(item => item.Id > 0)
            .Select(item => item.Id)
            .Distinct()
            .OrderBy(item => item)
            .ToArray();
        var related = findings
            .SelectMany(item => ParseRelatedIds(item.RelatedPerformanceIdsJson))
            .Distinct()
            .ToArray();
        var performanceId = related.Length == 1 ? related[0] : 0L;
        var actionKey = performanceId > 0
            ? PayrollStandbyActivityTypes.AdjustActionKey(primary.ResourceId, primary.Date, performanceId)
            : primary.FindingKey;

        return new PayrollActionEvidenceSnapshot(
            actionKey,
            primary.FindingType,
            findings.Max(item => item.Severity),
            primary.GpsClassification,
            string.Join(" || ", findings.Select(item => item.Evidence).Where(item => !string.IsNullOrWhiteSpace(item))),
            findings.Count == 1
                ? primary.Title
                : "Wachtdienst start/eind correctie (geaggregeerd)",
            string.Join(" ", findings.Select(item => item.Description).Where(item => !string.IsNullOrWhiteSpace(item))),
            related,
            findings.Select(item => item.SuggestedPayableStart).Where(item => item.HasValue).Min(),
            findings.Select(item => item.SuggestedPayableEnd).Where(item => item.HasValue).Max(),
            null,
            primary.SuggestedProjectId,
            primary.SuggestedBonNr,
            keys,
            ids);
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
            finding.SuggestedBonNr,
            SourceFindingKeys: [finding.FindingKey],
            SourceFindingIds: finding.Id > 0 ? [finding.Id] : null);

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
            PayrollProposedActionType.DeleteExistingPerformance =>
                "TimeControl payrollcontrole — prestatie verwijderen",
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
