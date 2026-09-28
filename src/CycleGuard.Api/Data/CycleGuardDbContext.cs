using CycleGuard.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace CycleGuard.Api.Data;

public class CycleGuardDbContext(DbContextOptions<CycleGuardDbContext> options) : DbContext(options)
{
    public DbSet<Job> Jobs => Set<Job>();

    public DbSet<JobAttempt> JobAttempts => Set<JobAttempt>();

    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    public DbSet<DownstreamLedgerEntry> DownstreamLedger => Set<DownstreamLedgerEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Job>(job =>
        {
            job.HasKey(j => j.Id);

            // Enums are stored as text so the raw-SQL atomic claim can compare against
            // readable literals like 'Queued' instead of magic integers.
            job.Property(j => j.Type).HasConversion<string>().HasMaxLength(64).IsRequired();
            job.Property(j => j.State).HasConversion<string>().HasMaxLength(32).IsRequired();
            job.Property(j => j.LastFailureClass).HasConversion<string?>().HasMaxLength(32);

            job.Property(j => j.IdempotencyKey).HasMaxLength(128).IsRequired();
            job.Property(j => j.DownstreamIdempotencyKey).HasMaxLength(128).IsRequired();
            job.Property(j => j.DownstreamEndpoint).HasMaxLength(64).IsRequired();
            job.Property(j => j.Payload).IsRequired();

            // One job per idempotency key, enforced by the database rather than by code.
            job.HasIndex(j => j.IdempotencyKey).IsUnique();

            // The claim query orders by deadline within the claimable states.
            job.HasIndex(j => new { j.State, j.DeadlineTicks });
            job.HasIndex(j => new { j.State, j.NextAttemptTicks });
            job.HasIndex(j => j.DownstreamEndpoint);
            job.HasIndex(j => j.LeaseExpiresTicks);

            job.HasMany(j => j.AttemptLog)
                .WithOne(a => a.Job!)
                .HasForeignKey(a => a.JobId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<JobAttempt>(attempt =>
        {
            attempt.HasKey(a => a.Id);
            attempt.Property(a => a.Outcome).HasConversion<string?>().HasMaxLength(32);
            attempt.Property(a => a.FailureClass).HasConversion<string?>().HasMaxLength(32);
            attempt.Property(a => a.WorkerId).HasMaxLength(64);
            attempt.HasIndex(a => new { a.JobId, a.AttemptNumber }).IsUnique();
        });

        modelBuilder.Entity<AuditEvent>(audit =>
        {
            audit.HasKey(e => e.Id);
            audit.Property(e => e.Actor).HasMaxLength(128).IsRequired();
            audit.Property(e => e.EventType).HasMaxLength(64).IsRequired();
            audit.HasIndex(e => new { e.JobId, e.AtTicks });
            audit.HasIndex(e => e.EventType);
        });

        modelBuilder.Entity<DownstreamLedgerEntry>(entry =>
        {
            entry.HasKey(e => e.Id);
            entry.Property(e => e.IdempotencyKey).HasMaxLength(128).IsRequired();
            entry.Property(e => e.Endpoint).HasMaxLength(64).IsRequired();

            // This is the constraint that makes a retried or requeued payment unable to
            // disburse twice.
            entry.HasIndex(e => e.IdempotencyKey).IsUnique();
        });
    }
}
