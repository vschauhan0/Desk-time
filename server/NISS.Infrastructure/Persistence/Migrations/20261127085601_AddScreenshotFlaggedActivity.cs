using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NISS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddScreenshotFlaggedActivity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "clip_id",
                table: "screenshots",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "clip_seq",
                table: "screenshots",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "clip_total",
                table: "screenshots",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "flag_term",
                table: "screenshots",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "matched_on",
                table: "screenshots",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "trigger",
                table: "screenshots",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "PERIODIC");

            migrationBuilder.CreateIndex(
                name: "ix_screenshots_clip_id_clip_seq",
                table: "screenshots",
                columns: new[] { "clip_id", "clip_seq" });

            migrationBuilder.CreateIndex(
                name: "ix_screenshots_device_id_trigger_captured_at_utc",
                table: "screenshots",
                columns: new[] { "device_id", "trigger", "captured_at_utc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_screenshots_clip_id_clip_seq",
                table: "screenshots");

            migrationBuilder.DropIndex(
                name: "ix_screenshots_device_id_trigger_captured_at_utc",
                table: "screenshots");

            migrationBuilder.DropColumn(
                name: "clip_id",
                table: "screenshots");

            migrationBuilder.DropColumn(
                name: "clip_seq",
                table: "screenshots");

            migrationBuilder.DropColumn(
                name: "clip_total",
                table: "screenshots");

            migrationBuilder.DropColumn(
                name: "flag_term",
                table: "screenshots");

            migrationBuilder.DropColumn(
                name: "matched_on",
                table: "screenshots");

            migrationBuilder.DropColumn(
                name: "trigger",
                table: "screenshots");
        }
    }
}
