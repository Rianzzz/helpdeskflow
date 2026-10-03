using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TenantProvisioningSaga : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "failure_reason",
                schema: "identity",
                table: "tenants",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "status",
                schema: "identity",
                table: "tenants",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Active"); // empresas anteriores à saga já estavam em uso: continuam ativas

            // Preserva o significado do campo antigo antes de removê-lo.
            migrationBuilder.Sql("UPDATE identity.tenants SET status = 'Failed' WHERE is_active = false;");

            migrationBuilder.DropColumn(
                name: "is_active",
                schema: "identity",
                table: "tenants");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "failure_reason",
                schema: "identity",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "status",
                schema: "identity",
                table: "tenants");

            migrationBuilder.AddColumn<bool>(
                name: "is_active",
                schema: "identity",
                table: "tenants",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }
    }
}
