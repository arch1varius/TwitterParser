using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Parser.Infrastructure.Migrations;

[DbContext(typeof(ParserDbContext))]
[Migration("20260927120000_AddTelegramSource")]
public sealed partial class AddTelegramSource : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex("IX_Authors_SourceId", "Authors");
        migrationBuilder.DropIndex("IX_Posts_SourceId", "Posts");
        foreach (var table in new[] { "Authors", "Posts", "Jobs" })
            migrationBuilder.AddColumn<string>("Source", table, type: "text", nullable: false, defaultValue: "X");
        migrationBuilder.AlterColumn<string>("Username", "Authors", type: "character varying(32)",
            maxLength: 32, nullable: false, oldClrType: typeof(string), oldType: "character varying(15)", oldMaxLength: 15);
        migrationBuilder.AlterColumn<string>("Author", "Jobs", type: "character varying(32)",
            maxLength: 32, nullable: false, oldClrType: typeof(string), oldType: "character varying(15)", oldMaxLength: 15);
        migrationBuilder.CreateIndex("IX_Authors_Source_SourceId", "Authors", new[] { "Source", "SourceId" }, unique: true);
        migrationBuilder.CreateIndex("IX_Posts_Source_SourceId", "Posts", new[] { "Source", "SourceId" }, unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // A downgrade cannot represent Telegram records. Refuse instead of deleting or merging their data.
        migrationBuilder.Sql("""
            DO $$ BEGIN
                IF EXISTS (SELECT 1 FROM "Authors" WHERE "Source" <> 'X')
                    OR EXISTS (SELECT 1 FROM "Posts" WHERE "Source" <> 'X')
                    OR EXISTS (SELECT 1 FROM "Jobs" WHERE "Source" <> 'X') THEN
                    RAISE EXCEPTION 'Cannot downgrade while Telegram data exists';
                END IF;
            END $$;
            """);
        migrationBuilder.DropIndex("IX_Authors_Source_SourceId", "Authors");
        migrationBuilder.DropIndex("IX_Posts_Source_SourceId", "Posts");
        foreach (var table in new[] { "Authors", "Posts", "Jobs" })
            migrationBuilder.DropColumn("Source", table);
        migrationBuilder.AlterColumn<string>("Username", "Authors", type: "character varying(15)",
            maxLength: 15, nullable: false, oldClrType: typeof(string), oldType: "character varying(32)", oldMaxLength: 32);
        migrationBuilder.AlterColumn<string>("Author", "Jobs", type: "character varying(15)",
            maxLength: 15, nullable: false, oldClrType: typeof(string), oldType: "character varying(32)", oldMaxLength: 32);
        migrationBuilder.CreateIndex("IX_Authors_SourceId", "Authors", "SourceId", unique: true);
        migrationBuilder.CreateIndex("IX_Posts_SourceId", "Posts", "SourceId", unique: true);
    }
}
