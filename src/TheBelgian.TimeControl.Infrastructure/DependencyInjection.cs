using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TheBelgian.TimeControl.Core.Configuration;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Payroll.Interfaces;
using TheBelgian.TimeControl.Core.Services;
using TheBelgian.TimeControl.Infrastructure.AdminReview;
using TheBelgian.TimeControl.Infrastructure.Authentication;
using TheBelgian.TimeControl.Infrastructure.Configuration;
using TheBelgian.TimeControl.Infrastructure.Geocoding;
using TheBelgian.TimeControl.Infrastructure.Persistence;
using TheBelgian.TimeControl.Infrastructure.Pilot;
using TheBelgian.TimeControl.Infrastructure.Plenion;
using TheBelgian.TimeControl.Infrastructure.Powerfleet;
using TheBelgian.TimeControl.Infrastructure.Payroll.Shadow;
using TheBelgian.TimeControl.Infrastructure.Payroll.Sources;
using TheBelgian.TimeControl.Infrastructure.Synchronization;
using TheBelgian.TimeControl.Infrastructure.VehicleAssignments;

namespace TheBelgian.TimeControl.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddTimeControlInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<MatchingOptions>()
            .Bind(configuration.GetSection(MatchingOptions.SectionName))
            .Validate(options =>
            {
                try
                {
                    options.Validate();
                    return true;
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            }, "Matchingtoleranties moeten oplopend zijn.")
            .ValidateOnStart();
        services.Configure<PlenionOptions>(
            configuration.GetSection(PlenionOptions.SectionName));
        services.AddOptions<PowerfleetOptions>()
            .Bind(configuration.GetSection(PowerfleetOptions.SectionName))
            .Validate(
                options => string.IsNullOrWhiteSpace(options.BaseUrl) ||
                           Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out _),
                "Powerfleet:BaseUrl moet leeg of een absolute URL zijn.");
        services.Configure<VehicleAssignmentReviewOptions>(
            configuration.GetSection(VehicleAssignmentReviewOptions.SectionName));
        services.AddOptions<AdminReviewWorkflowOptions>()
            .Bind(configuration.GetSection(AdminReviewWorkflowOptions.SectionName))
            .Validate(options => !string.IsNullOrWhiteSpace(options.DefaultReviewer),
                "AdminReview:DefaultReviewer ontbreekt.")
            .ValidateOnStart();
        services.AddOptions<PayrollShadowOptions>()
            .Bind(configuration.GetSection(PayrollShadowOptions.SectionName))
            .Validate(options =>
            {
                try
                {
                    options.Validate();
                    return true;
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            }, "PayrollShadow-configuratie is ongeldig.")
            .ValidateOnStart();
        services.AddOptions<CloudflareAccessOptions>()
            .Bind(configuration.GetSection(CloudflareAccessOptions.SectionName))
            .Validate(options => !options.Enabled ||
                                 (!string.IsNullOrWhiteSpace(options.TeamDomain) &&
                                  !string.IsNullOrWhiteSpace(options.Audience)),
                "CloudflareAccess:TeamDomain en Audience zijn verplicht wanneer Enabled=true.")
            .ValidateOnStart();
        services.AddHttpClient(nameof(CloudflareAccessCertificateProvider));
        services.AddSingleton<ICloudflareAccessCertificateProvider, CloudflareAccessCertificateProvider>();
        services.AddSingleton<ICloudflareAccessJwtValidator, CloudflareAccessJwtValidator>();
        services.AddOptions<TimeControlCorrectionWriteOptions>()
            .Bind(configuration.GetSection(TimeControlCorrectionWriteOptions.SectionName))
            .Validate(options => options.TimeoutSeconds > 0,
                "TimeControlCorrectionWrites:TimeoutSeconds moet positief zijn.")
            .Validate(options => !options.Enabled || options.UseMock ||
                                 Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out _),
                "TimeControlCorrectionWrites:BaseUrl moet een absolute URL zijn wanneer writes actief zijn.")
            .ValidateOnStart();
        services.AddOptions<GeocodingOptions>()
            .Bind(configuration.GetSection(GeocodingOptions.SectionName));
        services.AddOptions<LocationMatchingOptions>()
            .Bind(configuration.GetSection(LocationMatchingOptions.SectionName))
            .Validate(options =>
            {
                try
                {
                    options.Validate();
                    return true;
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            }, "Locatieafstandsgrenzen moeten positief en oplopend zijn.")
            .ValidateOnStart();
        services.AddOptions<AdaptiveLocationMatchingOptions>()
            .Bind(configuration.GetSection(AdaptiveLocationMatchingOptions.SectionName))
            .Validate(options =>
            {
                try
                {
                    options.Validate();
                    return true;
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            }, "Adaptieve locatieparameters moeten geldig zijn.")
            .ValidateOnStart();
        services.AddOptions<ReviewDataOptions>()
            .Bind(configuration.GetSection(ReviewDataOptions.SectionName))
            .Validate(options =>
            {
                try
                {
                    options.Validate();
                    return true;
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            }, "ReviewData-configuratie is ongeldig.")
            .ValidateOnStart();
        var sqliteConnection = configuration.GetConnectionString("TimeControl")
            ?? "Data Source=data/time-control.db";
        services.AddDbContextFactory<TimeControlDbContext>(options =>
            options.UseSqlite(sqliteConnection));

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(provider =>
            provider.GetRequiredService<IOptions<MatchingOptions>>().Value);
        services.AddSingleton(provider =>
            provider.GetRequiredService<IOptions<LocationMatchingOptions>>().Value);
        services.AddSingleton<IDistanceCalculator, HaversineDistanceCalculator>();
        services.AddScoped<ITimeControlMatchingService, TimeControlMatchingService>();
        services.AddScoped<IPlenionReader, OdbcPlenionReader>();
        services.AddSingleton<PowerfleetXmlParser>();
        services.AddHttpClient<IPowerfleetClient, PowerfleetClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(60);
        });
        services.AddScoped<IExceptionRepository, ExceptionRepository>();
        services.AddScoped<ISourceDataRepository, SourceDataRepository>();
        services.AddScoped<ISynchronizationService, SynchronizationService>();
        services.AddScoped<PilotPlenionReader>();
        services.AddHttpClient<AzureMapsGeocodingService>(
            client =>
            {
                client.BaseAddress = new Uri("https://atlas.microsoft.com/");
                client.Timeout = TimeSpan.FromSeconds(30);
            });
        services.AddHttpClient<GeoapifyGeocodingService>(
            client =>
            {
                client.BaseAddress = new Uri("https://api.geoapify.com/");
                client.Timeout = TimeSpan.FromSeconds(30);
            });
        services.AddScoped<IGeocodingService>(provider =>
        {
            var providerName = provider
                .GetRequiredService<IOptions<GeocodingOptions>>()
                .Value
                .Provider;
            return providerName.Equals(
                "Geoapify",
                StringComparison.OrdinalIgnoreCase)
                ? provider.GetRequiredService<GeoapifyGeocodingService>()
                : provider.GetRequiredService<AzureMapsGeocodingService>();
        });
        services.AddScoped<LocationGeocodingCache>();
        services.AddScoped<LocationResolutionPilotService>();
        services.AddHttpClient<PilotPowerfleetReader>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(90);
        });
        services.AddScoped<IReadOnlyPilotService, ReadOnlyPilotService>();
        services.AddScoped<IBroaderValidationPilotService, BroaderValidationPilotService>();
        services.AddScoped<LocationMatchingBenchmarkService>();
        services.AddScoped<CalibrationSingleReviewerEvaluationService>();
        services.AddScoped<LocationMatchingRecoveryAuditService>();
        services.AddScoped<HoursAuditService>();
        services.AddScoped<DailyHoursAuditService>();
        services.AddScoped<TechnicianVehicleAssignmentService>();
        services.AddScoped<TechnicianVehicleAssignmentBackfillService>();
        services.AddHttpClient<PowerfleetVehicleReader>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(60);
        });
        services.AddScoped<TechnicianVehicleAssignmentSyncService>();
        services.AddScoped<VehicleAssignmentSyncHistoryService>();
        services.AddScoped<HistoricalVehicleAssignmentCandidateService>();
        services.AddSingleton<HistoricalVehicleCandidateCache>();
        services.AddScoped<HistoricalVehicleAssignmentWorkflowService>();
        services.AddScoped<TechnicianTrackingEligibilityService>();
        services.AddScoped<DailyBoundaryContextIndexProvider>();
        services.AddScoped<KnownWorkLocationAuditService>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IReviewExplanationService, DeterministicReviewExplanationService>();
        services.AddScoped<OfflineReviewCaseProvider>();
        services.AddScoped<LiveReviewCaseProvider>();
        services.AddScoped<IReviewCaseProvider>(provider =>
        {
            var reviewData = provider.GetRequiredService<IOptions<ReviewDataOptions>>().Value;
            reviewData.Validate();
            return reviewData.IsLivePilot
                ? provider.GetRequiredService<LiveReviewCaseProvider>()
                : provider.GetRequiredService<OfflineReviewCaseProvider>();
        });
        services.AddScoped<AdminReviewDecisionRepository>();
        services.AddScoped<AdminReviewSessionMetricRepository>();
        services.AddScoped<IAdminReviewService, AdminReviewService>();
        services.AddSingleton<DailyAuditReviewCaseProvider>();
        services.AddScoped<DailyReviewRepository>();
        services.AddScoped<IDailyReviewService, DailyReviewService>();
        services.AddHttpClient<HttpPlenionCorrectionClient>((provider, client) =>
        {
            var options = provider.GetRequiredService<IOptions<TimeControlCorrectionWriteOptions>>().Value;
            if (Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var baseAddress))
                client.BaseAddress = baseAddress;
            client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        });
        services.AddScoped<MockPlenionCorrectionClient>();
        services.AddScoped<IPlenionCorrectionClient>(provider =>
            provider.GetRequiredService<IOptions<TimeControlCorrectionWriteOptions>>().Value.UseMock
                ? provider.GetRequiredService<MockPlenionCorrectionClient>()
                : provider.GetRequiredService<HttpPlenionCorrectionClient>());
        services.AddScoped<IMonthlyReviewService, MonthlyReviewService>();
        services.AddScoped<PayrollShadowCalculationService>();
        services.AddScoped<IPayrollResourceReader, PlenionPayrollResourceReader>();
        services.AddScoped<PlenionPayrollReader>();
        services.AddScoped<PlenionPayrollCalendarReader>();
        services.AddScoped<IPayrollPerformanceSource>(provider =>
            provider.GetRequiredService<PlenionPayrollReader>());
        services.AddScoped<IPayrollCalendarSource>(provider =>
            provider.GetRequiredService<PlenionPayrollCalendarReader>());
        services.AddScoped<IPayrollShadowService, PayrollShadowService>();
        return services;
    }

    public static async Task InitializeTimeControlDatabaseAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var factory = scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<TimeControlDbContext>>();
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        var databasePath = context.Database.GetDbConnection().DataSource;
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await context.Database.EnsureCreatedAsync(cancellationToken);
        await context.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "LocationResolutionCacheEntries" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_LocationResolutionCacheEntries" PRIMARY KEY AUTOINCREMENT,
                "DeliveryAddressExternalId" TEXT NULL,
                "OriginalAddress" TEXT NOT NULL,
                "NormalizedAddress" TEXT NOT NULL,
                "AddressHash" TEXT NOT NULL,
                "Latitude" REAL NULL,
                "Longitude" REAL NULL,
                "ResolvedAddress" TEXT NULL,
                "Confidence" TEXT NULL,
                "Provider" TEXT NOT NULL,
                "Status" INTEGER NOT NULL,
                "ErrorMessage" TEXT NULL,
                "AlternativesJson" TEXT NULL,
                "LastAttemptAt" TEXT NULL,
                "LastSuccessfulResolutionAt" TEXT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_LocationResolutionCacheEntries_AddressHash"
                ON "LocationResolutionCacheEntries" ("AddressHash");
            CREATE TABLE IF NOT EXISTS "AdminReviewDecisionAudits" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_AdminReviewDecisionAudits" PRIMARY KEY AUTOINCREMENT,
                "PerformanceId" INTEGER NOT NULL,
                "OriginalMatcherStatus" TEXT NOT NULL,
                "ProposedVisitCandidateId" TEXT NULL,
                "ProposedVisitSourceStopIdsJson" TEXT NULL,
                "ChosenVisitCandidateId" TEXT NULL,
                "ChosenVisitSourceStopIdsJson" TEXT NULL,
                "Decision" TEXT NOT NULL,
                "ReasonOrComment" TEXT NULL,
                "Reviewer" TEXT NOT NULL,
                "DecidedAt" TEXT NOT NULL,
                "MatcherCommit" TEXT NOT NULL,
                "ConfigurationHash" TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_AdminReviewDecisionAudits_PerformanceId_DecidedAt"
                ON "AdminReviewDecisionAudits" ("PerformanceId", "DecidedAt");
            CREATE TABLE IF NOT EXISTS "AdminReviewSessionMetrics" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_AdminReviewSessionMetrics" PRIMARY KEY AUTOINCREMENT,
                "PerformanceId" INTEGER NOT NULL,
                "OpenedAt" TEXT NOT NULL,
                "DecidedAt" TEXT NULL,
                "DurationSeconds" REAL NULL,
                "Decision" TEXT NULL,
                "MatcherStatus" TEXT NULL,
                "ProposedCandidateConfirmed" INTEGER NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_AdminReviewSessionMetrics_PerformanceId_OpenedAt"
                ON "AdminReviewSessionMetrics" ("PerformanceId", "OpenedAt");
            CREATE TABLE IF NOT EXISTS "DailyReviewActionAudits" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_DailyReviewActionAudits" PRIMARY KEY AUTOINCREMENT,
                "CaseId" TEXT NOT NULL,
                "Technician" TEXT NOT NULL,
                "Date" TEXT NOT NULL,
                "Decision" TEXT NOT NULL,
                "DecisionReason" TEXT NULL,
                "Notes" TEXT NULL,
                "ReviewedBy" TEXT NOT NULL,
                "ReviewedAt" TEXT NOT NULL,
                "EvidenceSnapshotJson" TEXT NOT NULL,
                "AlgorithmVersion" TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_DailyReviewActionAudits_CaseId_ReviewedAt"
                ON "DailyReviewActionAudits" ("CaseId", "ReviewedAt");
            CREATE TABLE IF NOT EXISTS "DailyCorrectionProposals" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_DailyCorrectionProposals" PRIMARY KEY AUTOINCREMENT,
                "CaseId" TEXT NOT NULL,
                "OriginalStart" TEXT NOT NULL,
                "OriginalEnd" TEXT NOT NULL,
                "ProposedStart" TEXT NULL,
                "ProposedEnd" TEXT NULL,
                "Reason" TEXT NOT NULL,
                "Notes" TEXT NULL,
                "ProposedBy" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "Status" TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_DailyCorrectionProposals_CaseId_CreatedAt"
                ON "DailyCorrectionProposals" ("CaseId", "CreatedAt");
            CREATE TABLE IF NOT EXISTS "DailyGeneratedFactualReports" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_DailyGeneratedFactualReports" PRIMARY KEY AUTOINCREMENT,
                "Technician" TEXT NOT NULL,
                "CaseIdsJson" TEXT NOT NULL,
                "Content" TEXT NOT NULL,
                "GeneratedBy" TEXT NOT NULL,
                "GeneratedAt" TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_DailyGeneratedFactualReports_Technician_GeneratedAt"
                ON "DailyGeneratedFactualReports" ("Technician", "GeneratedAt");
            CREATE TABLE IF NOT EXISTS "PhysicalVehicles" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_PhysicalVehicles" PRIMARY KEY AUTOINCREMENT,
                "ObjectId" TEXT NOT NULL,
                "RegistrationPlate" TEXT NULL,
                "Name" TEXT NOT NULL,
                "Make" TEXT NULL,
                "Model" TEXT NULL,
                "FirstObservedAt" TEXT NOT NULL,
                "LastObservedAt" TEXT NOT NULL,
                "IsActive" INTEGER NOT NULL,
                "Source" TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_PhysicalVehicles_ObjectId"
                ON "PhysicalVehicles" ("ObjectId");
            CREATE TABLE IF NOT EXISTS "TechnicianVehicleAssignments" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_TechnicianVehicleAssignments" PRIMARY KEY AUTOINCREMENT,
                "TechnicianExternalId" TEXT NOT NULL,
                "TechnicianCode" TEXT NOT NULL,
                "ObjectId" TEXT NOT NULL,
                "RegistrationPlateSnapshot" TEXT NULL,
                "ValidFrom" TEXT NOT NULL,
                "ValidTo" TEXT NULL,
                "Source" TEXT NOT NULL,
                "Confidence" TEXT NOT NULL,
                "ObservedAt" TEXT NOT NULL,
                "PreviousObservedAt" TEXT NULL,
                "EvidenceReference" TEXT NULL,
                "CreatedAt" TEXT NOT NULL,
                "ReviewedBy" TEXT NULL,
                "ReviewedAt" TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_TechnicianVehicleAssignments_TechnicianExternalId_ValidFrom"
                ON "TechnicianVehicleAssignments" ("TechnicianExternalId", "ValidFrom");
            CREATE INDEX IF NOT EXISTS "IX_TechnicianVehicleAssignments_ObjectId_ValidFrom"
                ON "TechnicianVehicleAssignments" ("ObjectId", "ValidFrom");
            CREATE TABLE IF NOT EXISTS "TechnicianVehicleAssignmentAudits" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_TechnicianVehicleAssignmentAudits" PRIMARY KEY AUTOINCREMENT,
                "AssignmentId" INTEGER NULL,
                "Action" TEXT NOT NULL,
                "Actor" TEXT NOT NULL,
                "Source" TEXT NOT NULL,
                "ChangedAt" TEXT NOT NULL,
                "OldAssignmentJson" TEXT NULL,
                "NewAssignmentJson" TEXT NULL,
                "EvidenceReference" TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_TechnicianVehicleAssignmentAudits_AssignmentId_ChangedAt"
                ON "TechnicianVehicleAssignmentAudits" ("AssignmentId", "ChangedAt");
            CREATE TABLE IF NOT EXISTS "TechnicianTrackingEligibilities" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_TechnicianTrackingEligibilities" PRIMARY KEY AUTOINCREMENT,
                "TechnicianExternalId" TEXT NOT NULL,
                "TechnicianCode" TEXT NOT NULL,
                "TrackingStatus" INTEGER NOT NULL,
                "Reason" TEXT NOT NULL,
                "Source" TEXT NOT NULL,
                "ValidFrom" TEXT NOT NULL,
                "ValidTo" TEXT NULL,
                "CreatedAt" TEXT NOT NULL,
                "CreatedBy" TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_TechnicianTrackingEligibilities_TechnicianExternalId_ValidFrom"
                ON "TechnicianTrackingEligibilities" ("TechnicianExternalId", "ValidFrom");
            CREATE TABLE IF NOT EXISTS "VehicleAssignmentSyncRuns" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_VehicleAssignmentSyncRuns" PRIMARY KEY AUTOINCREMENT,
                "StartedAt" TEXT NOT NULL,
                "FinishedAt" TEXT NULL,
                "Status" TEXT NOT NULL,
                "DurationSeconds" REAL NULL,
                "VehiclesRead" INTEGER NOT NULL,
                "PhysicalVehiclesObserved" INTEGER NOT NULL,
                "ExactMapped" INTEGER NOT NULL,
                "AssignmentsOpened" INTEGER NOT NULL,
                "AssignmentsObserved" INTEGER NOT NULL,
                "AssignmentsClosed" INTEGER NOT NULL,
                "Ambiguous" INTEGER NOT NULL,
                "Unmapped" INTEGER NOT NULL,
                "ResourcesWithoutPersonalVehicle" INTEGER NOT NULL,
                "SkippedNoTrackAndTrace" INTEGER NOT NULL,
                "ErrorSummary" TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_VehicleAssignmentSyncRuns_Status_FinishedAt"
                ON "VehicleAssignmentSyncRuns" ("Status", "FinishedAt");
            CREATE TABLE IF NOT EXISTS "MonthlyReviewPeriods" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_MonthlyReviewPeriods" PRIMARY KEY AUTOINCREMENT,
                "Year" INTEGER NOT NULL,
                "Month" INTEGER NOT NULL,
                "Status" INTEGER NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "PreparedAt" TEXT NULL,
                "LastRefreshedAt" TEXT NULL,
                "FinalizedAt" TEXT NULL,
                "FinalizedBy" TEXT NULL,
                "AlgorithmVersion" TEXT NOT NULL,
                "SourceCutoffAt" TEXT NULL,
                "LastVehicleSyncAt" TEXT NULL,
                "SummaryJson" TEXT NOT NULL,
                "FinalSnapshotJson" TEXT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_MonthlyReviewPeriods_Year_Month"
                ON "MonthlyReviewPeriods" ("Year", "Month");
            CREATE TABLE IF NOT EXISTS "MonthlyReviewCaseSnapshots" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_MonthlyReviewCaseSnapshots" PRIMARY KEY AUTOINCREMENT,
                "MonthlyReviewPeriodId" INTEGER NOT NULL,
                "CaseId" TEXT NOT NULL,
                "Technician" TEXT NOT NULL,
                "Date" TEXT NOT NULL,
                "EvidenceHash" TEXT NOT NULL,
                "EvidenceSnapshotJson" TEXT NOT NULL,
                "CaseJson" TEXT NOT NULL,
                "PreviousEvidenceSnapshotJson" TEXT NULL,
                "NeedsReReview" INTEGER NOT NULL,
                "IsActive" INTEGER NOT NULL,
                "UpdatedAt" TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_MonthlyReviewCaseSnapshots_Period_Case"
                ON "MonthlyReviewCaseSnapshots" ("MonthlyReviewPeriodId", "CaseId");
            CREATE TABLE IF NOT EXISTS "PayrollEmployeeConfigurationRecords" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_PayrollEmployeeConfigurationRecords" PRIMARY KEY AUTOINCREMENT,
                "ResourceId" TEXT NOT NULL,
                "ValidFrom" TEXT NOT NULL,
                "ValidTo" TEXT NULL,
                "EligibilityStatus" INTEGER NOT NULL,
                "ReasonCode" TEXT NOT NULL,
                "Comment" TEXT NULL,
                "DecisionSource" INTEGER NOT NULL,
                "CreatedAtUtc" TEXT NOT NULL,
                "CreatedBy" TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_PayrollEmployeeConfigurationRecords_ResourceId_ValidFrom"
                ON "PayrollEmployeeConfigurationRecords" ("ResourceId", "ValidFrom");
            CREATE TABLE IF NOT EXISTS "PayrollShadowMonths" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_PayrollShadowMonths" PRIMARY KEY AUTOINCREMENT,
                "Year" INTEGER NOT NULL,
                "Month" INTEGER NOT NULL,
                "PeriodStart" TEXT NOT NULL,
                "PeriodEnd" TEXT NOT NULL,
                "EvaluationDate" TEXT NOT NULL,
                "Status" INTEGER NOT NULL,
                "CalculationVersion" TEXT NOT NULL,
                "CreatedAtUtc" TEXT NOT NULL,
                "CreatedBy" TEXT NOT NULL,
                "LastReviewedAtUtc" TEXT NULL,
                "LastReviewedBy" TEXT NULL,
                "FinalizedAtUtc" TEXT NULL,
                "FinalizedBy" TEXT NULL,
                "ConfigurationSnapshotJson" TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_PayrollShadowMonths_Year_Month"
                ON "PayrollShadowMonths" ("Year", "Month");
            CREATE TABLE IF NOT EXISTS "PayrollShadowEmployeeResults" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_PayrollShadowEmployeeResults" PRIMARY KEY AUTOINCREMENT,
                "ShadowMonthId" INTEGER NOT NULL,
                "ResourceId" TEXT NOT NULL,
                "DisplayNameSnapshot" TEXT NOT NULL,
                "ResourceCodeSnapshot" TEXT NOT NULL,
                "EmailSnapshot" TEXT NULL,
                "EligibilityStatus" INTEGER NOT NULL,
                "EligibilityReason" TEXT NULL,
                "SuggestedEligibility" INTEGER NULL,
                "SuggestedReason" TEXT NULL,
                "LegacyTheoreticalHours" REAL NULL,
                "LegacyActualOrdinaryHours" REAL NULL,
                "LegacyDifferenceHours" REAL NULL,
                "StandbyExactHours" REAL NULL,
                "StandbyRoundedHours" REAL NULL,
                "Code135At150Units" REAL NULL,
                "Code135At200Units" REAL NULL,
                "CityTripUnits" INTEGER NULL,
                "CityAllowanceAmount" REAL NULL,
                "EligibleKm" REAL NULL,
                "Extra75LegacyValue" REAL NULL,
                "KmRate" REAL NULL,
                "KmAmount" REAL NULL,
                "Code414Amount" REAL NULL,
                "AcertaIdentityStatus" INTEGER NOT NULL,
                "OrdinaryStatus" INTEGER NOT NULL,
                "StandbyStatus" INTEGER NOT NULL,
                "CityStatus" INTEGER NOT NULL,
                "KmStatus" INTEGER NOT NULL,
                "Code414Status" INTEGER NOT NULL,
                "ReviewStatus" INTEGER NOT NULL,
                "ReviewComment" TEXT NULL,
                "ReviewedAtUtc" TEXT NULL,
                "ReviewedBy" TEXT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_PayrollShadowEmployeeResults_Month_Resource"
                ON "PayrollShadowEmployeeResults" ("ShadowMonthId", "ResourceId");
            CREATE TABLE IF NOT EXISTS "PayrollShadowReviewAudits" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_PayrollShadowReviewAudits" PRIMARY KEY AUTOINCREMENT,
                "ShadowMonthId" INTEGER NOT NULL,
                "ResourceId" TEXT NULL,
                "Action" INTEGER NOT NULL,
                "Actor" TEXT NOT NULL,
                "TimestampUtc" TEXT NOT NULL,
                "ReasonCode" TEXT NULL,
                "Comment" TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_PayrollShadowReviewAudits_Month_Timestamp"
                ON "PayrollShadowReviewAudits" ("ShadowMonthId", "TimestampUtc");
            """,
            cancellationToken);
        await EnsureColumnAsync(
            context,
            "TechnicianVehicleAssignments",
            "ReviewedBy",
            "TEXT NULL",
            cancellationToken);
        foreach (var column in new (string Name, string Definition)[]
                 {
                     ("FirstPerformanceId", "INTEGER NOT NULL DEFAULT 0"),
                     ("LastPerformanceId", "INTEGER NOT NULL DEFAULT 0"),
                     ("FirstActivityType", "TEXT NOT NULL DEFAULT ''"),
                     ("LastActivityType", "TEXT NOT NULL DEFAULT ''"),
                     ("FirstMainTaskExternalId", "INTEGER NULL"),
                     ("LastMainTaskExternalId", "INTEGER NULL"),
                     ("FirstRecordOriginalStart", "TEXT NULL"),
                     ("FirstRecordOriginalEnd", "TEXT NULL"),
                     ("LastRecordOriginalStart", "TEXT NULL"),
                     ("LastRecordOriginalEnd", "TEXT NULL"),
                     ("ExecutedStart", "TEXT NULL"),
                     ("ExecutedEnd", "TEXT NULL"),
                     ("ExecutedBy", "TEXT NULL"),
                     ("ExecutedAt", "TEXT NULL"),
                     ("PlenionWriteReference", "TEXT NULL"),
                     ("PlenionWriteResponse", "TEXT NULL"),
                     ("ErrorMessage", "TEXT NULL"),
                 })
        {
            await EnsureColumnAsync(context, "DailyCorrectionProposals", column.Name,
                column.Definition, cancellationToken);
        }
        await EnsureColumnAsync(
            context,
            "TechnicianVehicleAssignments",
            "ReviewedAt",
            "TEXT NULL",
            cancellationToken);
        foreach (var column in new (string Table, string Name, string Definition)[]
                 {
                     ("DailyReviewActionAudits", "ReviewedBySubject", "TEXT NULL"),
                     ("DailyCorrectionProposals", "ProposedBySubject", "TEXT NULL"),
                     ("DailyCorrectionProposals", "ExecutedBySubject", "TEXT NULL"),
                     ("AdminReviewDecisionAudits", "ReviewerSubject", "TEXT NULL"),
                 })
        {
            await EnsureColumnAsync(context, column.Table, column.Name, column.Definition, cancellationToken);
        }
    }

    private static async Task EnsureColumnAsync(
        TimeControlDbContext context,
        string table,
        string column,
        string definition,
        CancellationToken cancellationToken)
    {
        var connection = context.Database.GetDbConnection();
        var closeAfter = connection.State != System.Data.ConnectionState.Open;
        if (closeAfter) await connection.OpenAsync(cancellationToken);
        try
        {
            await using var check = connection.CreateCommand();
            check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}'";
            var exists = Convert.ToInt32(
                await check.ExecuteScalarAsync(cancellationToken),
                System.Globalization.CultureInfo.InvariantCulture) > 0;
            if (exists) return;
            await using var alter = connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {definition}";
            await alter.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            if (closeAfter) await connection.CloseAsync();
        }
    }
}
