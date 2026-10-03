using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TenantUserLimit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "max_users",
                schema: "identity",
                table: "tenants",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "user_count",
                schema: "identity",
                table: "tenants",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Dados que JÁ existem: o contador passa a refletir a realidade e as empresas ativas recebem o limite do
            // plano Free (5), o único plano que existia até agora. Empresas novas recebem o limite pelo evento.
            migrationBuilder.Sql(@"
                UPDATE identity.tenants t
                SET user_count = (SELECT COUNT(*) FROM identity.users u WHERE u.tenant_id = t.id),
                    max_users  = CASE WHEN t.status = 'Active' THEN 5 ELSE 0 END;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "max_users",
                schema: "identity",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "user_count",
                schema: "identity",
                table: "tenants");
        }
    }
}
