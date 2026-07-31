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

        modelBuilder.Entity<PlenionPerformance>().Property(item => item.Kilometres).HasPrecision(12, 3);
        modelBuilder.Entity<PowerfleetTrip>().Property(item => item.DistanceKilometres).HasPrecision(12, 3);
        modelBuilder.Entity<DetectedException>().Property(item => item.PowerfleetDistanceKilometres).HasPrecision(12, 3);
    }
}
