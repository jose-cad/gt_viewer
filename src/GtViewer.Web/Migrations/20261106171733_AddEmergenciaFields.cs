using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GtViewer.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddEmergenciaFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EmergenciaExpiraEm",
                table: "Tecnicos",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsContaEmergencia",
                table: "Tecnicos",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "SenhaLocalHash",
                table: "Tecnicos",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EmergenciaExpiraEm",
                table: "Tecnicos");

            migrationBuilder.DropColumn(
                name: "IsContaEmergencia",
                table: "Tecnicos");

            migrationBuilder.DropColumn(
                name: "SenhaLocalHash",
                table: "Tecnicos");
        }
    }
}
