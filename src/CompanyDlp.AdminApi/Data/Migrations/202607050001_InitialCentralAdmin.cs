using CompanyDlp.AdminApi.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CompanyDlp.AdminApi.Data.Migrations;

[DbContext(typeof(CompanyDlpDbContext))]
[Migration("202607050001_InitialCentralAdmin")]
public partial class InitialCentralAdmin : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Tenants",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                PolicyRevision = table.Column<long>(type: "bigint", nullable: false),
                IsActive = table.Column<bool>(type: "bit", nullable: false),
                CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_Tenants", x => x.Id));

        migrationBuilder.CreateTable(
            name: "AdminUsers",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                NormalizedEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                DisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                PasswordHashBase64 = table.Column<string>(type: "nvarchar(max)", nullable: false),
                PasswordSaltBase64 = table.Column<string>(type: "nvarchar(max)", nullable: false),
                Role = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                TokenVersion = table.Column<int>(type: "int", nullable: false),
                IsActive = table.Column<bool>(type: "bit", nullable: false),
                CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                LastLoginAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AdminUsers", x => x.Id);
                table.ForeignKey(
                    name: "FK_AdminUsers_Tenants_TenantId",
                    column: x => x.TenantId,
                    principalTable: "Tenants",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "Employees",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                EmployeeNumber = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                DisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                Username = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                WindowsSid = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                Department = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                IsActive = table.Column<bool>(type: "bit", nullable: false),
                CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Employees", x => x.Id);
                table.ForeignKey(
                    name: "FK_Employees_Tenants_TenantId",
                    column: x => x.TenantId,
                    principalTable: "Tenants",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "PermissionGrants",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ActionKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                Allowed = table.Column<bool>(type: "bit", nullable: false),
                ScopeType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                ScopeId = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                Source = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                Priority = table.Column<int>(type: "int", nullable: false),
                StartsAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                ExpiresAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                GrantedBy = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                RevokedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                RevokedBy = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PermissionGrants", x => x.Id);
                table.ForeignKey(
                    name: "FK_PermissionGrants_Tenants_TenantId",
                    column: x => x.TenantId,
                    principalTable: "Tenants",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "EnrollmentCodes",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                CodeHashHex = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                Description = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                ExpiresAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                UsedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                CreatedByAdminUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_EnrollmentCodes", x => x.Id);
                table.ForeignKey(
                    name: "FK_EnrollmentCodes_AdminUsers_CreatedByAdminUserId",
                    column: x => x.CreatedByAdminUserId,
                    principalTable: "AdminUsers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_EnrollmentCodes_Tenants_TenantId",
                    column: x => x.TenantId,
                    principalTable: "Tenants",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "Devices",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                EmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                MachineName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                AgentVersion = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                OsVersion = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                TokenHashHex = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                TokenExpiresAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                IsActive = table.Column<bool>(type: "bit", nullable: false),
                EnrolledAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                LastSeenAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                LastAppliedPolicyVersion = table.Column<long>(type: "bigint", nullable: false),
                PendingAuditEventCount = table.Column<int>(type: "int", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Devices", x => x.Id);
                table.ForeignKey(
                    name: "FK_Devices_Employees_EmployeeId",
                    column: x => x.EmployeeId,
                    principalTable: "Employees",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
                table.ForeignKey(
                    name: "FK_Devices_Tenants_TenantId",
                    column: x => x.TenantId,
                    principalTable: "Tenants",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "TenantPolicies",
            columns: table => new
            {
                TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                PolicyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                PolicyJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                UpdatedByAdminUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_TenantPolicies", x => x.TenantId);
                table.ForeignKey(
                    name: "FK_TenantPolicies_AdminUsers_UpdatedByAdminUserId",
                    column: x => x.UpdatedByAdminUserId,
                    principalTable: "AdminUsers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
                table.ForeignKey(
                    name: "FK_TenantPolicies_Tenants_TenantId",
                    column: x => x.TenantId,
                    principalTable: "Tenants",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "AdminAuditLogs",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                AdminUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                AdminEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                Action = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                TargetType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                TargetId = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                DetailsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                IpAddress = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                OccurredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AdminAuditLogs", x => x.Id);
                table.ForeignKey(
                    name: "FK_AdminAuditLogs_AdminUsers_AdminUserId",
                    column: x => x.AdminUserId,
                    principalTable: "AdminUsers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
                table.ForeignKey(
                    name: "FK_AdminAuditLogs_Tenants_TenantId",
                    column: x => x.TenantId,
                    principalTable: "Tenants",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "SecurityEvents",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                EventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                CorrelationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                ActionKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                EventType = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                Decision = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                ReasonCode = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                OccurredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                ReceivedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                PayloadJson = table.Column<string>(type: "nvarchar(max)", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_SecurityEvents", x => x.Id);
                table.ForeignKey(
                    name: "FK_SecurityEvents_Devices_DeviceId",
                    column: x => x.DeviceId,
                    principalTable: "Devices",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_SecurityEvents_Tenants_TenantId",
                    column: x => x.TenantId,
                    principalTable: "Tenants",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex("IX_AdminAuditLogs_AdminUserId", "AdminAuditLogs", "AdminUserId");
        migrationBuilder.CreateIndex("IX_AdminAuditLogs_TenantId_OccurredAtUtc", "AdminAuditLogs", new[] { "TenantId", "OccurredAtUtc" });
        migrationBuilder.CreateIndex("IX_AdminUsers_NormalizedEmail", "AdminUsers", "NormalizedEmail", unique: true);
        migrationBuilder.CreateIndex("IX_AdminUsers_TenantId_Role", "AdminUsers", new[] { "TenantId", "Role" });
        migrationBuilder.CreateIndex("IX_Devices_EmployeeId", "Devices", "EmployeeId");
        migrationBuilder.CreateIndex("IX_Devices_TenantId_MachineName", "Devices", new[] { "TenantId", "MachineName" });
        migrationBuilder.CreateIndex("IX_Devices_TokenHashHex", "Devices", "TokenHashHex");
        migrationBuilder.CreateIndex("IX_Employees_TenantId_EmployeeNumber", "Employees", new[] { "TenantId", "EmployeeNumber" }, unique: true);
        migrationBuilder.CreateIndex("IX_Employees_TenantId_Username", "Employees", new[] { "TenantId", "Username" });
        migrationBuilder.CreateIndex("IX_Employees_TenantId_WindowsSid", "Employees", new[] { "TenantId", "WindowsSid" });
        migrationBuilder.CreateIndex("IX_EnrollmentCodes_CodeHashHex", "EnrollmentCodes", "CodeHashHex", unique: true);
        migrationBuilder.CreateIndex("IX_EnrollmentCodes_CreatedByAdminUserId", "EnrollmentCodes", "CreatedByAdminUserId");
        migrationBuilder.CreateIndex("IX_EnrollmentCodes_TenantId_ExpiresAtUtc", "EnrollmentCodes", new[] { "TenantId", "ExpiresAtUtc" });
        migrationBuilder.CreateIndex("IX_PermissionGrants_TenantId_ActionKey_ScopeType_ScopeId", "PermissionGrants", new[] { "TenantId", "ActionKey", "ScopeType", "ScopeId" });
        migrationBuilder.CreateIndex("IX_PermissionGrants_TenantId_RevokedAtUtc_ExpiresAtUtc", "PermissionGrants", new[] { "TenantId", "RevokedAtUtc", "ExpiresAtUtc" });
        migrationBuilder.CreateIndex("IX_SecurityEvents_DeviceId", "SecurityEvents", "DeviceId");
        migrationBuilder.CreateIndex("IX_SecurityEvents_TenantId_DeviceId_OccurredAtUtc", "SecurityEvents", new[] { "TenantId", "DeviceId", "OccurredAtUtc" });
        migrationBuilder.CreateIndex("IX_SecurityEvents_TenantId_EventId", "SecurityEvents", new[] { "TenantId", "EventId" }, unique: true);
        migrationBuilder.CreateIndex("IX_TenantPolicies_UpdatedByAdminUserId", "TenantPolicies", "UpdatedByAdminUserId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("AdminAuditLogs");
        migrationBuilder.DropTable("EnrollmentCodes");
        migrationBuilder.DropTable("PermissionGrants");
        migrationBuilder.DropTable("SecurityEvents");
        migrationBuilder.DropTable("TenantPolicies");
        migrationBuilder.DropTable("Devices");
        migrationBuilder.DropTable("AdminUsers");
        migrationBuilder.DropTable("Employees");
        migrationBuilder.DropTable("Tenants");
    }
}
