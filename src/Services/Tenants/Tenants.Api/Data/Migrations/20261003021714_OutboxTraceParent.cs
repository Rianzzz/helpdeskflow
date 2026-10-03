using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tenants.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class OutboxTraceParent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "trace_parent",
                schema: "tenants",
                table: "outbox_messages",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "trace_parent",
                schema: "tenants",
                table: "outbox_messages");
        }
    }
}
