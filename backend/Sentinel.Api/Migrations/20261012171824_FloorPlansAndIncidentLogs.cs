using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sentinel.Api.Migrations
{
    /// <inheritdoc />
    public partial class FloorPlansAndIncidentLogs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FloorPlans",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Building = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    FloorNumber = table.Column<int>(type: "int", nullable: false),
                    WidthMeters = table.Column<double>(type: "float", nullable: false),
                    HeightMeters = table.Column<double>(type: "float", nullable: false),
                    ImageFileName = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FloorPlans", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "IncidentLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    IncidentId = table.Column<int>(type: "int", nullable: true),
                    Type = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Timestamp = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Zone = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Details = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    FireAreaPercent = table.Column<double>(type: "float", nullable: true),
                    FireSeverity = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    HumanCount = table.Column<int>(type: "int", nullable: true),
                    DangerCount = table.Column<int>(type: "int", nullable: true),
                    PersonTrackId = table.Column<string>(type: "nvarchar(48)", maxLength: 48, nullable: true),
                    PersonX = table.Column<double>(type: "float", nullable: true),
                    PersonY = table.Column<double>(type: "float", nullable: true),
                    DangerZone = table.Column<string>(type: "nvarchar(48)", maxLength: 48, nullable: true),
                    Simulated = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IncidentLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IncidentLogs_IncidentAlerts_IncidentId",
                        column: x => x.IncidentId,
                        principalTable: "IncidentAlerts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FloorPlanElements",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FloorPlanId = table.Column<int>(type: "int", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    X = table.Column<double>(type: "float", nullable: false),
                    Y = table.Column<double>(type: "float", nullable: false),
                    Width = table.Column<double>(type: "float", nullable: false),
                    Height = table.Column<double>(type: "float", nullable: false),
                    IsBlocked = table.Column<bool>(type: "bit", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    Zone = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FloorPlanElements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FloorPlanElements_FloorPlans_FloorPlanId",
                        column: x => x.FloorPlanId,
                        principalTable: "FloorPlans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FloorPlanElements_FloorPlanId",
                table: "FloorPlanElements",
                column: "FloorPlanId");

            migrationBuilder.CreateIndex(
                name: "IX_IncidentLogs_IncidentId_Timestamp",
                table: "IncidentLogs",
                columns: new[] { "IncidentId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_IncidentLogs_Type_Timestamp",
                table: "IncidentLogs",
                columns: new[] { "Type", "Timestamp" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FloorPlanElements");

            migrationBuilder.DropTable(
                name: "IncidentLogs");

            migrationBuilder.DropTable(
                name: "FloorPlans");
        }
    }
}
