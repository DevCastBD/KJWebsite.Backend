using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FinanceService.Migrations
{
    /// <inheritdoc />
    public partial class Banking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "bank_accounts",
                schema: "finance",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    BankName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    NumberProtected = table.Column<string>(type: "text", nullable: false),
                    Last4 = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    LedgerAccountId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    CsvMapping = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ArchivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bank_accounts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_bank_accounts_accounts_LedgerAccountId",
                        column: x => x.LedgerAccountId,
                        principalSchema: "finance",
                        principalTable: "accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "bank_line_links",
                schema: "finance",
                columns: table => new
                {
                    LineId = table.Column<long>(type: "bigint", nullable: false),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    LedgerEntryId = table.Column<long>(type: "bigint", nullable: true),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Automatic = table.Column<bool>(type: "boolean", nullable: false),
                    By = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bank_line_links", x => x.LineId);
                });

            migrationBuilder.CreateTable(
                name: "bank_match_events",
                schema: "finance",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LineId = table.Column<long>(type: "bigint", nullable: false),
                    Action = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    LedgerEntryId = table.Column<long>(type: "bigint", nullable: true),
                    PostingId = table.Column<long>(type: "bigint", nullable: true),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    By = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bank_match_events", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "bank_reveals",
                schema: "finance",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BankAccountId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    By = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bank_reveals", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "bank_statements",
                schema: "finance",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BankAccountId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    FileHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Format = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    FileName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    LineCount = table.Column<int>(type: "integer", nullable: false),
                    DuplicateCount = table.Column<int>(type: "integer", nullable: false),
                    FromDate = table.Column<DateOnly>(type: "date", nullable: true),
                    ToDate = table.Column<DateOnly>(type: "date", nullable: true),
                    ImportedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ImportedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bank_statements", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "bank_lines",
                schema: "finance",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    StatementId = table.Column<long>(type: "bigint", nullable: false),
                    BankAccountId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Balance = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Occurrence = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bank_lines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_bank_lines_bank_statements_StatementId",
                        column: x => x.StatementId,
                        principalSchema: "finance",
                        principalTable: "bank_statements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_bank_accounts_LedgerAccountId",
                schema: "finance",
                table: "bank_accounts",
                column: "LedgerAccountId",
                unique: true,
                filter: "\"ArchivedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_bank_line_links_LedgerEntryId",
                schema: "finance",
                table: "bank_line_links",
                column: "LedgerEntryId",
                unique: true,
                filter: "\"LedgerEntryId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_bank_lines_BankAccountId_Fingerprint_Occurrence",
                schema: "finance",
                table: "bank_lines",
                columns: new[] { "BankAccountId", "Fingerprint", "Occurrence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_bank_lines_StatementId",
                schema: "finance",
                table: "bank_lines",
                column: "StatementId");

            migrationBuilder.CreateIndex(
                name: "IX_bank_match_events_LineId",
                schema: "finance",
                table: "bank_match_events",
                column: "LineId");

            migrationBuilder.CreateIndex(
                name: "IX_bank_statements_BankAccountId_FileHash",
                schema: "finance",
                table: "bank_statements",
                columns: new[] { "BankAccountId", "FileHash" },
                unique: true);

            // Statements, lines, match events and reveals are evidence and append-only; amounts are never zero (PostgreSQL only, like the other guards).
            if (migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.Ordinal) == true)
            {
                migrationBuilder.Sql("""
                    ALTER TABLE finance.bank_lines ADD CONSTRAINT ck_bank_lines_nonzero CHECK ("Amount" <> 0);
                    CREATE TRIGGER bank_statements_append_only BEFORE UPDATE OR DELETE ON finance.bank_statements
                        FOR EACH ROW EXECUTE FUNCTION finance.refuse_ledger_change();
                    CREATE TRIGGER bank_lines_append_only BEFORE UPDATE OR DELETE ON finance.bank_lines
                        FOR EACH ROW EXECUTE FUNCTION finance.refuse_ledger_change();
                    CREATE TRIGGER bank_match_events_append_only BEFORE UPDATE OR DELETE ON finance.bank_match_events
                        FOR EACH ROW EXECUTE FUNCTION finance.refuse_ledger_change();
                    CREATE TRIGGER bank_reveals_append_only BEFORE UPDATE OR DELETE ON finance.bank_reveals
                        FOR EACH ROW EXECUTE FUNCTION finance.refuse_ledger_change();
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.Ordinal) == true)
            {
                migrationBuilder.Sql("DROP TRIGGER bank_reveals_append_only ON finance.bank_reveals; DROP TRIGGER bank_match_events_append_only ON finance.bank_match_events; DROP TRIGGER bank_lines_append_only ON finance.bank_lines; DROP TRIGGER bank_statements_append_only ON finance.bank_statements;");
            }

            migrationBuilder.DropTable(
                name: "bank_accounts",
                schema: "finance");

            migrationBuilder.DropTable(
                name: "bank_line_links",
                schema: "finance");

            migrationBuilder.DropTable(
                name: "bank_lines",
                schema: "finance");

            migrationBuilder.DropTable(
                name: "bank_match_events",
                schema: "finance");

            migrationBuilder.DropTable(
                name: "bank_reveals",
                schema: "finance");

            migrationBuilder.DropTable(
                name: "bank_statements",
                schema: "finance");
        }
    }
}
