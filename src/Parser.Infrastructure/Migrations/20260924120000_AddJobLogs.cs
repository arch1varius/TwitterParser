using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Parser.Infrastructure.Migrations;

public partial class AddJobLogs : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "JobLogs",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                JobId = table.Column<Guid>(type: "uuid", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                SourceId = table.Column<string>(type: "text", nullable: false),
                Username = table.Column<string>(type: "text", nullable: false),
                PublishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                TextPreview = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                WordCount = table.Column<int>(type: "integer", nullable: false),
                Reason = table.Column<string>(type: "text", nullable: false),
                Detail = table.Column<string>(type: "text", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_JobLogs", x => x.Id);
                table.ForeignKey("FK_JobLogs_Jobs_JobId", x => x.JobId, "Jobs", "Id", onDelete: ReferentialAction.Cascade);
            });
        migrationBuilder.CreateIndex("IX_JobLogs_JobId_Id", "JobLogs", new[] { "JobId", "Id" });
        migrationBuilder.CreateIndex("IX_JobLogs_JobId_Reason_Id", "JobLogs", new[] { "JobId", "Reason", "Id" });
    }

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("JobLogs");
}
