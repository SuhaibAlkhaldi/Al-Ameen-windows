using CompanyDlp.AdminApi.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CompanyDlp.AdminApi.Data.Migrations;

[DbContext(typeof(CompanyDlpDbContext))]
partial class CompanyDlpDbContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder)
    {
        #pragma warning disable 612, 618
        modelBuilder
            .HasAnnotation("ProductVersion", "8.0.11")
            .HasAnnotation("Relational:MaxIdentifierLength", 128);

        SqlServerModelBuilderExtensions.UseIdentityColumns(modelBuilder);

        modelBuilder.Entity("CompanyDlp.AdminApi.Domain.AdminAuditLogEntity", b =>
        {
            b.Property<long>("Id")
                .ValueGeneratedOnAdd()
                .HasColumnType("bigint");

            SqlServerPropertyBuilderExtensions.UseIdentityColumn(b.Property<long>("Id"));
            b.Property<string>("Action")
                .IsRequired()
                .HasMaxLength(200)
                .HasColumnType("nvarchar(200)");
            b.Property<string>("AdminEmail")
                .IsRequired()
                .HasMaxLength(320)
                .HasColumnType("nvarchar(320)");
            b.Property<Guid?>("AdminUserId")
                .HasColumnType("uniqueidentifier");
            b.Property<string>("DetailsJson")
                .IsRequired()
                .HasColumnType("nvarchar(max)");
            b.Property<string>("IpAddress")
                .IsRequired()
                .HasMaxLength(100)
                .HasColumnType("nvarchar(100)");
            b.Property<DateTimeOffset>("OccurredAtUtc")
                .HasColumnType("datetimeoffset");
            b.Property<string>("TargetId")
                .IsRequired()
                .HasMaxLength(300)
                .HasColumnType("nvarchar(300)");
            b.Property<string>("TargetType")
                .IsRequired()
                .HasMaxLength(100)
                .HasColumnType("nvarchar(100)");
            b.Property<Guid>("TenantId")
                .HasColumnType("uniqueidentifier");

            b.HasKey("Id");
            b.HasIndex("AdminUserId");
            b.HasIndex("TenantId", "OccurredAtUtc");
            b.ToTable("AdminAuditLogs");
        });

        modelBuilder.Entity("CompanyDlp.AdminApi.Domain.AdminUserEntity", b =>
        {
            b.Property<Guid>("Id")
                .HasColumnType("uniqueidentifier");
            b.Property<DateTimeOffset>("CreatedAtUtc")
                .HasColumnType("datetimeoffset");
            b.Property<string>("DisplayName")
                .IsRequired()
                .HasMaxLength(200)
                .HasColumnType("nvarchar(200)");
            b.Property<string>("Email")
                .IsRequired()
                .HasMaxLength(320)
                .HasColumnType("nvarchar(320)");
            b.Property<bool>("IsActive")
                .HasColumnType("bit");
            b.Property<DateTimeOffset?>("LastLoginAtUtc")
                .HasColumnType("datetimeoffset");
            b.Property<string>("NormalizedEmail")
                .IsRequired()
                .HasMaxLength(320)
                .HasColumnType("nvarchar(320)");
            b.Property<string>("PasswordHashBase64")
                .IsRequired()
                .HasColumnType("nvarchar(max)");
            b.Property<string>("PasswordSaltBase64")
                .IsRequired()
                .HasColumnType("nvarchar(max)");
            b.Property<string>("Role")
                .IsRequired()
                .HasMaxLength(50)
                .HasColumnType("nvarchar(50)");
            b.Property<Guid>("TenantId")
                .HasColumnType("uniqueidentifier");
            b.Property<int>("TokenVersion")
                .HasColumnType("int");

            b.HasKey("Id");
            b.HasIndex("NormalizedEmail").IsUnique();
            b.HasIndex("TenantId", "Role");
            b.ToTable("AdminUsers");
        });

        modelBuilder.Entity("CompanyDlp.AdminApi.Domain.DeviceEntity", b =>
        {
            b.Property<Guid>("Id")
                .HasColumnType("uniqueidentifier");
            b.Property<string>("AgentVersion")
                .IsRequired()
                .HasMaxLength(50)
                .HasColumnType("nvarchar(50)");
            b.Property<Guid?>("EmployeeId")
                .HasColumnType("uniqueidentifier");
            b.Property<DateTimeOffset>("EnrolledAtUtc")
                .HasColumnType("datetimeoffset");
            b.Property<bool>("IsActive")
                .HasColumnType("bit");
            b.Property<long>("LastAppliedPolicyVersion")
                .HasColumnType("bigint");
            b.Property<DateTimeOffset?>("LastSeenAtUtc")
                .HasColumnType("datetimeoffset");
            b.Property<string>("MachineName")
                .IsRequired()
                .HasMaxLength(256)
                .HasColumnType("nvarchar(256)");
            b.Property<string>("OsVersion")
                .IsRequired()
                .HasMaxLength(500)
                .HasColumnType("nvarchar(500)");
            b.Property<int>("PendingAuditEventCount")
                .HasColumnType("int");
            b.Property<Guid>("TenantId")
                .HasColumnType("uniqueidentifier");
            b.Property<DateTimeOffset?>("TokenExpiresAtUtc")
                .HasColumnType("datetimeoffset");
            b.Property<string>("TokenHashHex")
                .IsRequired()
                .HasMaxLength(128)
                .HasColumnType("nvarchar(128)");

            b.HasKey("Id");
            b.HasIndex("EmployeeId");
            b.HasIndex("TenantId", "MachineName");
            b.HasIndex("TokenHashHex");
            b.ToTable("Devices");
        });

        modelBuilder.Entity("CompanyDlp.AdminApi.Domain.EmployeeEntity", b =>
        {
            b.Property<Guid>("Id")
                .HasColumnType("uniqueidentifier");
            b.Property<DateTimeOffset>("CreatedAtUtc")
                .HasColumnType("datetimeoffset");
            b.Property<string>("Department")
                .IsRequired()
                .HasMaxLength(200)
                .HasColumnType("nvarchar(200)");
            b.Property<string>("DisplayName")
                .IsRequired()
                .HasMaxLength(200)
                .HasColumnType("nvarchar(200)");
            b.Property<string>("EmployeeNumber")
                .IsRequired()
                .HasMaxLength(100)
                .HasColumnType("nvarchar(100)");
            b.Property<bool>("IsActive")
                .HasColumnType("bit");
            b.Property<Guid>("TenantId")
                .HasColumnType("uniqueidentifier");
            b.Property<DateTimeOffset>("UpdatedAtUtc")
                .HasColumnType("datetimeoffset");
            b.Property<string>("Username")
                .IsRequired()
                .HasMaxLength(256)
                .HasColumnType("nvarchar(256)");
            b.Property<string>("WindowsSid")
                .IsRequired()
                .HasMaxLength(256)
                .HasColumnType("nvarchar(256)");

            b.HasKey("Id");
            b.HasIndex("TenantId", "EmployeeNumber").IsUnique();
            b.HasIndex("TenantId", "Username");
            b.HasIndex("TenantId", "WindowsSid");
            b.ToTable("Employees");
        });

        modelBuilder.Entity("CompanyDlp.AdminApi.Domain.EnrollmentCodeEntity", b =>
        {
            b.Property<Guid>("Id")
                .HasColumnType("uniqueidentifier");
            b.Property<string>("CodeHashHex")
                .IsRequired()
                .HasMaxLength(128)
                .HasColumnType("nvarchar(128)");
            b.Property<DateTimeOffset>("CreatedAtUtc")
                .HasColumnType("datetimeoffset");
            b.Property<Guid>("CreatedByAdminUserId")
                .HasColumnType("uniqueidentifier");
            b.Property<string>("Description")
                .IsRequired()
                .HasMaxLength(200)
                .HasColumnType("nvarchar(200)");
            b.Property<DateTimeOffset>("ExpiresAtUtc")
                .HasColumnType("datetimeoffset");
            b.Property<Guid>("TenantId")
                .HasColumnType("uniqueidentifier");
            b.Property<DateTimeOffset?>("UsedAtUtc")
                .HasColumnType("datetimeoffset");

            b.HasKey("Id");
            b.HasIndex("CodeHashHex").IsUnique();
            b.HasIndex("CreatedByAdminUserId");
            b.HasIndex("TenantId", "ExpiresAtUtc");
            b.ToTable("EnrollmentCodes");
        });

        modelBuilder.Entity("CompanyDlp.AdminApi.Domain.PermissionGrantEntity", b =>
        {
            b.Property<Guid>("Id")
                .HasColumnType("uniqueidentifier");
            b.Property<string>("ActionKey")
                .IsRequired()
                .HasMaxLength(200)
                .HasColumnType("nvarchar(200)");
            b.Property<bool>("Allowed")
                .HasColumnType("bit");
            b.Property<DateTimeOffset>("CreatedAtUtc")
                .HasColumnType("datetimeoffset");
            b.Property<DateTimeOffset?>("ExpiresAtUtc")
                .HasColumnType("datetimeoffset");
            b.Property<string>("GrantedBy")
                .IsRequired()
                .HasMaxLength(320)
                .HasColumnType("nvarchar(320)");
            b.Property<int>("Priority")
                .HasColumnType("int");
            b.Property<string>("Reason")
                .IsRequired()
                .HasMaxLength(1000)
                .HasColumnType("nvarchar(1000)");
            b.Property<DateTimeOffset?>("RevokedAtUtc")
                .HasColumnType("datetimeoffset");
            b.Property<string>("RevokedBy")
                .IsRequired()
                .HasMaxLength(320)
                .HasColumnType("nvarchar(320)");
            b.Property<string>("ScopeId")
                .IsRequired()
                .HasMaxLength(300)
                .HasColumnType("nvarchar(300)");
            b.Property<string>("ScopeType")
                .IsRequired()
                .HasMaxLength(50)
                .HasColumnType("nvarchar(50)");
            b.Property<string>("Source")
                .IsRequired()
                .HasMaxLength(50)
                .HasColumnType("nvarchar(50)");
            b.Property<DateTimeOffset>("StartsAtUtc")
                .HasColumnType("datetimeoffset");
            b.Property<Guid>("TenantId")
                .HasColumnType("uniqueidentifier");
            b.Property<DateTimeOffset>("UpdatedAtUtc")
                .HasColumnType("datetimeoffset");

            b.HasKey("Id");
            b.HasIndex("TenantId", "ActionKey", "ScopeType", "ScopeId");
            b.HasIndex("TenantId", "RevokedAtUtc", "ExpiresAtUtc");
            b.ToTable("PermissionGrants");
        });

        modelBuilder.Entity("CompanyDlp.AdminApi.Domain.SecurityEventEntity", b =>
        {
            b.Property<long>("Id")
                .ValueGeneratedOnAdd()
                .HasColumnType("bigint");

            SqlServerPropertyBuilderExtensions.UseIdentityColumn(b.Property<long>("Id"));
            b.Property<string>("ActionKey")
                .IsRequired()
                .HasMaxLength(200)
                .HasColumnType("nvarchar(200)");
            b.Property<Guid>("CorrelationId")
                .HasColumnType("uniqueidentifier");
            b.Property<string>("Decision")
                .IsRequired()
                .HasMaxLength(50)
                .HasColumnType("nvarchar(50)");
            b.Property<Guid>("DeviceId")
                .HasColumnType("uniqueidentifier");
            b.Property<Guid>("EventId")
                .HasColumnType("uniqueidentifier");
            b.Property<string>("EventType")
                .IsRequired()
                .HasMaxLength(200)
                .HasColumnType("nvarchar(200)");
            b.Property<DateTimeOffset>("OccurredAtUtc")
                .HasColumnType("datetimeoffset");
            b.Property<string>("PayloadJson")
                .IsRequired()
                .HasColumnType("nvarchar(max)");
            b.Property<string>("ReasonCode")
                .IsRequired()
                .HasMaxLength(200)
                .HasColumnType("nvarchar(200)");
            b.Property<DateTimeOffset>("ReceivedAtUtc")
                .HasColumnType("datetimeoffset");
            b.Property<Guid>("TenantId")
                .HasColumnType("uniqueidentifier");
            b.Property<Guid?>("UserId")
                .HasColumnType("uniqueidentifier");

            b.HasKey("Id");
            b.HasIndex("DeviceId");
            b.HasIndex("TenantId", "DeviceId", "OccurredAtUtc");
            b.HasIndex("TenantId", "EventId").IsUnique();
            b.ToTable("SecurityEvents");
        });

        modelBuilder.Entity("CompanyDlp.AdminApi.Domain.TenantEntity", b =>
        {
            b.Property<Guid>("Id")
                .HasColumnType("uniqueidentifier");
            b.Property<DateTimeOffset>("CreatedAtUtc")
                .HasColumnType("datetimeoffset");
            b.Property<bool>("IsActive")
                .HasColumnType("bit");
            b.Property<string>("Name")
                .IsRequired()
                .HasMaxLength(200)
                .HasColumnType("nvarchar(200)");
            b.Property<long>("PolicyRevision")
                .HasColumnType("bigint");
            b.Property<byte[]>("RowVersion")
                .IsConcurrencyToken()
                .ValueGeneratedOnAddOrUpdate()
                .HasColumnType("rowversion");
            b.Property<DateTimeOffset>("UpdatedAtUtc")
                .HasColumnType("datetimeoffset");

            b.HasKey("Id");
            b.ToTable("Tenants");
        });

        modelBuilder.Entity("CompanyDlp.AdminApi.Domain.TenantPolicyEntity", b =>
        {
            b.Property<Guid>("TenantId")
                .HasColumnType("uniqueidentifier");
            b.Property<Guid>("PolicyId")
                .HasColumnType("uniqueidentifier");
            b.Property<string>("PolicyJson")
                .IsRequired()
                .HasColumnType("nvarchar(max)");
            b.Property<DateTimeOffset>("UpdatedAtUtc")
                .HasColumnType("datetimeoffset");
            b.Property<Guid?>("UpdatedByAdminUserId")
                .HasColumnType("uniqueidentifier");

            b.HasKey("TenantId");
            b.HasIndex("UpdatedByAdminUserId");
            b.ToTable("TenantPolicies");
        });

        modelBuilder.Entity("CompanyDlp.AdminApi.Domain.AdminAuditLogEntity", b =>
        {
            b.HasOne("CompanyDlp.AdminApi.Domain.AdminUserEntity", null)
                .WithMany()
                .HasForeignKey("AdminUserId")
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity("CompanyDlp.AdminApi.Domain.AdminAuditLogEntity", b =>
        {
            b.HasOne("CompanyDlp.AdminApi.Domain.TenantEntity", null)
                .WithMany()
                .HasForeignKey("TenantId")
                .OnDelete(DeleteBehavior.Restrict)
                .IsRequired();
        });

        modelBuilder.Entity("CompanyDlp.AdminApi.Domain.AdminUserEntity", b =>
        {
            b.HasOne("CompanyDlp.AdminApi.Domain.TenantEntity", null)
                .WithMany()
                .HasForeignKey("TenantId")
                .OnDelete(DeleteBehavior.Restrict)
                .IsRequired();
        });

        modelBuilder.Entity("CompanyDlp.AdminApi.Domain.DeviceEntity", b =>
        {
            b.HasOne("CompanyDlp.AdminApi.Domain.EmployeeEntity", "Employee")
                .WithMany("Devices")
                .HasForeignKey("EmployeeId")
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity("CompanyDlp.AdminApi.Domain.DeviceEntity", b =>
        {
            b.HasOne("CompanyDlp.AdminApi.Domain.TenantEntity", null)
                .WithMany()
                .HasForeignKey("TenantId")
                .OnDelete(DeleteBehavior.Restrict)
                .IsRequired();
        });

        modelBuilder.Entity("CompanyDlp.AdminApi.Domain.EmployeeEntity", b =>
        {
            b.HasOne("CompanyDlp.AdminApi.Domain.TenantEntity", null)
                .WithMany()
                .HasForeignKey("TenantId")
                .OnDelete(DeleteBehavior.Restrict)
                .IsRequired();
        });

        modelBuilder.Entity("CompanyDlp.AdminApi.Domain.EnrollmentCodeEntity", b =>
        {
            b.HasOne("CompanyDlp.AdminApi.Domain.AdminUserEntity", null)
                .WithMany()
                .HasForeignKey("CreatedByAdminUserId")
                .OnDelete(DeleteBehavior.Restrict)
                .IsRequired();
        });

        modelBuilder.Entity("CompanyDlp.AdminApi.Domain.EnrollmentCodeEntity", b =>
        {
            b.HasOne("CompanyDlp.AdminApi.Domain.TenantEntity", null)
                .WithMany()
                .HasForeignKey("TenantId")
                .OnDelete(DeleteBehavior.Restrict)
                .IsRequired();
        });

        modelBuilder.Entity("CompanyDlp.AdminApi.Domain.PermissionGrantEntity", b =>
        {
            b.HasOne("CompanyDlp.AdminApi.Domain.TenantEntity", null)
                .WithMany()
                .HasForeignKey("TenantId")
                .OnDelete(DeleteBehavior.Restrict)
                .IsRequired();
        });

        modelBuilder.Entity("CompanyDlp.AdminApi.Domain.SecurityEventEntity", b =>
        {
            b.HasOne("CompanyDlp.AdminApi.Domain.DeviceEntity", null)
                .WithMany()
                .HasForeignKey("DeviceId")
                .OnDelete(DeleteBehavior.Restrict)
                .IsRequired();
        });

        modelBuilder.Entity("CompanyDlp.AdminApi.Domain.SecurityEventEntity", b =>
        {
            b.HasOne("CompanyDlp.AdminApi.Domain.TenantEntity", null)
                .WithMany()
                .HasForeignKey("TenantId")
                .OnDelete(DeleteBehavior.Restrict)
                .IsRequired();
        });

        modelBuilder.Entity("CompanyDlp.AdminApi.Domain.TenantPolicyEntity", b =>
        {
            b.HasOne("CompanyDlp.AdminApi.Domain.AdminUserEntity", null)
                .WithMany()
                .HasForeignKey("UpdatedByAdminUserId")
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity("CompanyDlp.AdminApi.Domain.TenantPolicyEntity", b =>
        {
            b.HasOne("CompanyDlp.AdminApi.Domain.TenantEntity", "Tenant")
                .WithOne("Policy")
                .HasForeignKey("CompanyDlp.AdminApi.Domain.TenantPolicyEntity", "TenantId")
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired();
        });

        modelBuilder.Entity("CompanyDlp.AdminApi.Domain.DeviceEntity", b =>
        {
            b.Navigation("Employee");
        });

        modelBuilder.Entity("CompanyDlp.AdminApi.Domain.EmployeeEntity", b =>
        {
            b.Navigation("Devices");
        });

        modelBuilder.Entity("CompanyDlp.AdminApi.Domain.TenantEntity", b =>
        {
            b.Navigation("Policy");
        });

        modelBuilder.Entity("CompanyDlp.AdminApi.Domain.TenantPolicyEntity", b =>
        {
            b.Navigation("Tenant");
        });

        #pragma warning restore 612, 618
    }
}
