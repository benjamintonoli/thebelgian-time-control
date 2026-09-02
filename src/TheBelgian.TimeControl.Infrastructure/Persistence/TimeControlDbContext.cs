using Microsoft.EntityFrameworkCore;
using TheBelgian.TimeControl.Core.Models;

namespace TheBelgian.TimeControl.Infrastructure.Persistence;

public sealed class TimeControlDbContext(DbContextOptions<TimeControlDbContext> options)
    : DbContext(options)
{
    public DbSet<Technician> Technicians => Set<Technician>();
    public DbSet<Vehicle> Vehicles => Set<Vehicle>();
    public DbSet<VehicleAssignment> VehicleAssignments => Set<VehicleAssignment>();
    public DbSet<PlenionPerformance> PlenionPerformances => Set<PlenionPerformance>();
    public DbSet<PowerfleetTrip> PowerfleetTrips => Set<PowerfleetTrip>();
    public DbSet<CustomerLocation> CustomerLocations => Set<CustomerLocation>();
    public DbSet<DetectedException> DetectedExceptions => Set<DetectedException>();
    public DbSet<SynchronizationRun> SynchronizationRuns => Set<SynchronizationRun>();
    public DbSet<LocationResolutionCacheEntry> LocationResolutionCacheEntries =>
        Set<LocationResolutionCacheEntry>();
    public DbSet<AdminReviewDecisionAudit> AdminReviewDecisionAudits =>
        Set<AdminReviewDecisionAudit>();
    public DbSet<AdminReviewSessionMetric> AdminReviewSessionMetrics =>
        Set<AdminReviewSessionMetric>();
    public DbSet<DailyReviewActionAudit> DailyReviewActionAudits =>
        Set<DailyReviewActionAudit>();
    public DbSet<DailyCorrectionProposal> DailyCorrectionProposals =>
        Set<DailyCorrectionProposal>();
    public DbSet<DailyGeneratedFactualReport> DailyGeneratedFactualReports =>
        Set<DailyGeneratedFactualReport>();
    public DbSet<PhysicalVehicle> PhysicalVehicles => Set<PhysicalVehicle>();
    public DbSet<TechnicianVehicleAssignment> TechnicianVehicleAssignments =>
        Set<TechnicianVehicleAssignment>();
    public DbSet<TechnicianVehicleAssignmentAudit> TechnicianVehicleAssignmentAudits =>
        Set<TechnicianVehicleAssignmentAudit>();
    public DbSet<TechnicianTrackingEligibility> TechnicianTrackingEligibilities =>
        Set<TechnicianTrackingEligibility>();
    public DbSet<VehicleAssignmentSyncRun> VehicleAssignmentSyncRuns =>
        Set<VehicleAssignmentSyncRun>();
    public DbSet<MonthlyReviewPeriod> MonthlyReviewPeriods => Set<MonthlyReviewPeriod>();
    public DbSet<MonthlyReviewCaseSnapshot> MonthlyReviewCaseSnapshots =>
        Set<MonthlyReviewCaseSnapshot>();
    public DbSet<PayrollEmployeeConfigurationRecord> PayrollEmployeeConfigurationRecords =>
        Set<PayrollEmployeeConfigurationRecord>();
    public DbSet<PayrollShadowMonth> PayrollShadowMonths => Set<PayrollShadowMonth>();
    public DbSet<PayrollShadowEmployeeResult> PayrollShadowEmployeeResults =>
        Set<PayrollShadowEmployeeResult>();
    public DbSet<PayrollShadowReviewAudit> PayrollShadowReviewAudits =>
        Set<PayrollShadowReviewAudit>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Technician>().HasIndex(item => item.ExternalId).IsUnique();
        modelBuilder.Entity<Vehicle>().HasIndex(item => item.ExternalId).IsUnique();
        modelBuilder.Entity<PlenionPerformance>().HasIndex(item => item.ExternalId).IsUnique();
        modelBuilder.Entity<PowerfleetTrip>().HasIndex(item => item.ExternalId).IsUnique();
        modelBuilder.Entity<CustomerLocation>().HasIndex(item => item.ExternalId).IsUnique();
        modelBuilder.Entity<DetectedException>().HasIndex(item => item.ExternalKey).IsUnique();
        modelBuilder.Entity<LocationResolutionCacheEntry>()
            .HasIndex(item => item.AddressHash)
            .IsUnique();
        modelBuilder.Entity<VehicleAssignment>()
            .HasIndex(item => new { item.TechnicianId, item.VehicleId, item.ValidFrom })
            .IsUnique();
        modelBuilder.Entity<AdminReviewDecisionAudit>()
            .HasIndex(item => new { item.PerformanceId, item.DecidedAt });
        modelBuilder.Entity<AdminReviewSessionMetric>()
            .HasIndex(item => new { item.PerformanceId, item.OpenedAt });
        modelBuilder.Entity<DailyReviewActionAudit>()
            .HasIndex(item => new { item.CaseId, item.ReviewedAt });
        modelBuilder.Entity<DailyCorrectionProposal>()
            .HasIndex(item => new { item.CaseId, item.CreatedAt });
        modelBuilder.Entity<DailyGeneratedFactualReport>()
            .HasIndex(item => new { item.Technician, item.GeneratedAt });
        modelBuilder.Entity<PhysicalVehicle>()
            .HasIndex(item => item.ObjectId)
            .IsUnique();
        modelBuilder.Entity<TechnicianVehicleAssignment>()
            .HasIndex(item => new { item.TechnicianExternalId, item.ValidFrom });
        modelBuilder.Entity<TechnicianVehicleAssignment>()
            .HasIndex(item => new { item.ObjectId, item.ValidFrom });
        modelBuilder.Entity<TechnicianVehicleAssignmentAudit>()
            .HasIndex(item => new { item.AssignmentId, item.ChangedAt });
        modelBuilder.Entity<TechnicianTrackingEligibility>()
            .HasIndex(item => new { item.TechnicianExternalId, item.ValidFrom });
        modelBuilder.Entity<VehicleAssignmentSyncRun>()
            .HasIndex(item => new { item.Status, item.FinishedAt });
        modelBuilder.Entity<MonthlyReviewPeriod>()
            .HasIndex(item => new { item.Year, item.Month })
            .IsUnique();
        modelBuilder.Entity<MonthlyReviewCaseSnapshot>()
            .HasIndex(item => new { item.MonthlyReviewPeriodId, item.CaseId })
            .IsUnique();
        modelBuilder.Entity<PayrollEmployeeConfigurationRecord>()
            .HasIndex(item => new { item.ResourceId, item.ValidFrom });
        modelBuilder.Entity<PayrollShadowMonth>()
            .HasIndex(item => new { item.Year, item.Month })
            .IsUnique();
        modelBuilder.Entity<PayrollShadowEmployeeResult>()
            .HasIndex(item => new { item.ShadowMonthId, item.ResourceId })
            .IsUnique();
        modelBuilder.Entity<PayrollShadowReviewAudit>()
            .HasIndex(item => new { item.ShadowMonthId, item.TimestampUtc });

        modelBuilder.Entity<PlenionPerformance>().Property(item => item.Kilometres).HasPrecision(12, 3);
        modelBuilder.Entity<PowerfleetTrip>().Property(item => item.DistanceKilometres).HasPrecision(12, 3);
        modelBuilder.Entity<DetectedException>().Property(item => item.PowerfleetDistanceKilometres).HasPrecision(12, 3);
    }
}
