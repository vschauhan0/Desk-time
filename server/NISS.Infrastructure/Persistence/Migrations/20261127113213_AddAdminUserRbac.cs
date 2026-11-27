using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NISS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAdminUserRbac : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "allowed_departments_json",
                table: "admin_users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "allowed_employee_ids_json",
                table: "admin_users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "allowed_pages_json",
                table: "admin_users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "scope_all_employees",
                table: "admin_users",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "allowed_departments_json",
                table: "admin_users");

            migrationBuilder.DropColumn(
                name: "allowed_employee_ids_json",
                table: "admin_users");

            migrationBuilder.DropColumn(
                name: "allowed_pages_json",
                table: "admin_users");

            migrationBuilder.DropColumn(
                name: "scope_all_employees",
                table: "admin_users");
        }
    }
}
