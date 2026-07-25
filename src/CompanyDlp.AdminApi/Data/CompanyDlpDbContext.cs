using CompanyDlp.AdminApi.Domain;
using Microsoft.EntityFrameworkCore;

namespace CompanyDlp.AdminApi.Data;

public sealed class CompanyDlpDbContext(DbContextOptions<CompanyDlpDbContext> options) : DbContext(options)
{
    public DbSet<TenantEntity> Tenants => Set<TenantEntity>();
    public DbSet<TenantPolicyEntity> TenantPolicies => Set<TenantPolicyEntity>();
    public DbSet<AdminUserEntity> AdminUsers => Set<AdminUserEntity>();
    public DbSet<EmployeeEntity> Employees => Set<EmployeeEntity>();
    public DbSet<DeviceEntity> Devices => Set<DeviceEntity>();
    public DbSet<EnrollmentCodeEntity> EnrollmentCodes => Set<EnrollmentCodeEntity>();
    public DbSet<PermissionGrantEntity> PermissionGrants => Set<PermissionGrantEntity>();
    public DbSet<SecurityEventEntity> SecurityEvents => Set<SecurityEventEntity>();
    public DbSet<AdminAuditLogEntity> AdminAuditLogs => Set<AdminAuditLogEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TenantEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.RowVersion).IsRowVersion();
            entity.HasOne(x => x.Policy)
                .WithOne(x => x.Tenant)
                .HasForeignKey<TenantPolicyEntity>(x => x.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TenantPolicyEntity>(entity =>
        {
            entity.HasKey(x => x.TenantId);
            entity.Property(x => x.PolicyJson).HasColumnType("nvarchar(max)");
            entity.HasOne<AdminUserEntity>()
                .WithMany()
                .HasForeignKey(x => x.UpdatedByAdminUserId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<AdminUserEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.NormalizedEmail).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.Role });
            entity.HasOne<TenantEntity>()
                .WithMany()
                .HasForeignKey(x => x.TenantId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<EmployeeEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.EmployeeNumber }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.WindowsSid });
            entity.HasIndex(x => new { x.TenantId, x.Username });
            entity.HasOne<TenantEntity>()
                .WithMany()
                .HasForeignKey(x => x.TenantId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<DeviceEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.TokenHashHex);
            entity.HasIndex(x => new { x.TenantId, x.MachineName });
            entity.HasOne<TenantEntity>()
                .WithMany()
                .HasForeignKey(x => x.TenantId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.Employee)
                .WithMany(x => x.Devices)
                .HasForeignKey(x => x.EmployeeId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<EnrollmentCodeEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.CodeHashHex).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.ExpiresAtUtc });
            entity.HasOne<TenantEntity>()
                .WithMany()
                .HasForeignKey(x => x.TenantId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<AdminUserEntity>()
                .WithMany()
                .HasForeignKey(x => x.CreatedByAdminUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PermissionGrantEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.ActionKey, x.ScopeType, x.ScopeId });
            entity.HasIndex(x => new { x.TenantId, x.RevokedAtUtc, x.ExpiresAtUtc });
            entity.HasOne<TenantEntity>()
                .WithMany()
                .HasForeignKey(x => x.TenantId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SecurityEventEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.EventId }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.DeviceId, x.OccurredAtUtc });
            entity.Property(x => x.PayloadJson).HasColumnType("nvarchar(max)");
            entity.HasOne<TenantEntity>()
                .WithMany()
                .HasForeignKey(x => x.TenantId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<DeviceEntity>()
                .WithMany()
                .HasForeignKey(x => x.DeviceId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AdminAuditLogEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TenantId, x.OccurredAtUtc });
            entity.Property(x => x.DetailsJson).HasColumnType("nvarchar(max)");
            entity.HasOne<TenantEntity>()
                .WithMany()
                .HasForeignKey(x => x.TenantId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<AdminUserEntity>()
                .WithMany()
                .HasForeignKey(x => x.AdminUserId)
                .OnDelete(DeleteBehavior.SetNull);
        });
    }
}
