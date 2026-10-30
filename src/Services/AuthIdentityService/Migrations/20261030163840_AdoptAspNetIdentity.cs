using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AuthIdentityService.Migrations
{
    /// <inheritdoc />
    public partial class AdoptAspNetIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SecurityStamp",
                schema: "auth",
                table: "users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PhoneNumber",
                schema: "auth",
                table: "users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PasswordHash",
                schema: "auth",
                table: "users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ConcurrencyStamp",
                schema: "auth",
                table: "users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "TwoFactorEnabled",
                schema: "auth",
                table: "users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AlterColumn<string>(
                name: "Email",
                schema: "auth",
                table: "users",
                type: "character varying(320)",
                maxLength: 320,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(320)",
                oldMaxLength: 320);

            migrationBuilder.AddColumn<int>(
                name: "AccessFailedCount",
                schema: "auth",
                table: "users",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "EmailConfirmed",
                schema: "auth",
                table: "users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "LockoutEnabled",
                schema: "auth",
                table: "users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LockoutEnd",
                schema: "auth",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NormalizedEmail",
                schema: "auth",
                table: "users",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NormalizedUserName",
                schema: "auth",
                table: "users",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "PhoneNumberConfirmed",
                schema: "auth",
                table: "users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "UserName",
                schema: "auth",
                table: "users",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "member_profiles",
                schema: "auth",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    FirstName = table.Column<string>(type: "text", nullable: true),
                    LastName = table.Column<string>(type: "text", nullable: true),
                    Gender = table.Column<string>(type: "text", nullable: true),
                    ReasonForJoining = table.Column<string>(type: "text", nullable: true),
                    PresentOrganization = table.Column<string>(type: "text", nullable: true),
                    VolunteeingExperience = table.Column<string>(type: "text", nullable: true),
                    DateOfBirth = table.Column<string>(type: "text", nullable: true),
                    CityOfResidence = table.Column<string>(type: "text", nullable: true),
                    CountryOfResidence = table.Column<string>(type: "text", nullable: true),
                    PermanentAddress = table.Column<string>(type: "text", nullable: true),
                    MailingAddress = table.Column<string>(type: "text", nullable: true),
                    IsMailingAddressSameAsPermanentAddress = table.Column<bool>(type: "boolean", nullable: false),
                    BloodGroup = table.Column<string>(type: "text", nullable: true),
                    AreasOfExpertise = table.Column<string>(type: "text", nullable: true),
                    HighestDegree = table.Column<string>(type: "text", nullable: true),
                    DisabilitiesIfAny = table.Column<string>(type: "text", nullable: true),
                    Nationality = table.Column<string>(type: "text", nullable: true),
                    PersonalWebPage = table.Column<string>(type: "text", nullable: true),
                    SocialMediaLink = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_member_profiles", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_member_profiles_users_UserId",
                        column: x => x.UserId,
                        principalSchema: "auth",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "roles",
                schema: "auth",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    NormalizedName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_roles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "user_claims",
                schema: "auth",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<string>(type: "character varying(64)", nullable: false),
                    ClaimType = table.Column<string>(type: "text", nullable: true),
                    ClaimValue = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_claims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_user_claims_users_UserId",
                        column: x => x.UserId,
                        principalSchema: "auth",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_logins",
                schema: "auth",
                columns: table => new
                {
                    LoginProvider = table.Column<string>(type: "text", nullable: false),
                    ProviderKey = table.Column<string>(type: "text", nullable: false),
                    ProviderDisplayName = table.Column<string>(type: "text", nullable: true),
                    UserId = table.Column<string>(type: "character varying(64)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_logins", x => new { x.LoginProvider, x.ProviderKey });
                    table.ForeignKey(
                        name: "FK_user_logins_users_UserId",
                        column: x => x.UserId,
                        principalSchema: "auth",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_tokens",
                schema: "auth",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "character varying(64)", nullable: false),
                    LoginProvider = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Value = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_tokens", x => new { x.UserId, x.LoginProvider, x.Name });
                    table.ForeignKey(
                        name: "FK_user_tokens_users_UserId",
                        column: x => x.UserId,
                        principalSchema: "auth",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "role_claims",
                schema: "auth",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RoleId = table.Column<string>(type: "text", nullable: false),
                    ClaimType = table.Column<string>(type: "text", nullable: true),
                    ClaimValue = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_role_claims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_role_claims_roles_RoleId",
                        column: x => x.RoleId,
                        principalSchema: "auth",
                        principalTable: "roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_roles",
                schema: "auth",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "character varying(64)", nullable: false),
                    RoleId = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_roles", x => new { x.UserId, x.RoleId });
                    table.ForeignKey(
                        name: "FK_user_roles_roles_RoleId",
                        column: x => x.RoleId,
                        principalSchema: "auth",
                        principalTable: "roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_user_roles_users_UserId",
                        column: x => x.UserId,
                        principalSchema: "auth",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                schema: "auth",
                table: "users",
                column: "NormalizedEmail");

            migrationBuilder.CreateIndex(
                name: "UserNameIndex",
                schema: "auth",
                table: "users",
                column: "NormalizedUserName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_role_claims_RoleId",
                schema: "auth",
                table: "role_claims",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "RoleNameIndex",
                schema: "auth",
                table: "roles",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_user_claims_UserId",
                schema: "auth",
                table: "user_claims",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_user_logins_UserId",
                schema: "auth",
                table: "user_logins",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_user_roles_RoleId",
                schema: "auth",
                table: "user_roles",
                column: "RoleId");

            // One-way data step. Passwords are NOT carried over: plain-text credentials must be treated as
            // compromised, so every existing account has a null PasswordHash and must go through a reset.
            migrationBuilder.Sql(@"
                UPDATE auth.users SET
                    ""UserName"" = ""Email"",
                    ""NormalizedUserName"" = upper(""Email""),
                    ""NormalizedEmail"" = upper(""Email""),
                    ""SecurityStamp"" = md5(random()::text || clock_timestamp()::text),
                    ""ConcurrencyStamp"" = md5(random()::text || clock_timestamp()::text),
                    ""LockoutEnabled"" = true,
                    ""EmailConfirmed"" = false;

                INSERT INTO auth.member_profiles (""UserId"", ""FirstName"", ""LastName"", ""Gender"", ""ReasonForJoining"",
                    ""PresentOrganization"", ""VolunteeingExperience"", ""DateOfBirth"", ""CityOfResidence"", ""CountryOfResidence"",
                    ""PermanentAddress"", ""MailingAddress"", ""IsMailingAddressSameAsPermanentAddress"", ""BloodGroup"",
                    ""AreasOfExpertise"", ""HighestDegree"", ""DisabilitiesIfAny"", ""Nationality"", ""PersonalWebPage"", ""SocialMediaLink"")
                SELECT ""Id"", ""FirstName"", ""LastName"", ""Gender"", ""ReasonForJoining"",
                    ""PresentOrganization"", ""VolunteeingExperience"", ""DateOfBirth"", ""CityOfResidence"", ""CountryOfResidence"",
                    ""PermanentAddress"", ""MailingAddress"", ""IsMailingAddressSameAsPermanentAddress"", ""BloodGroup"",
                    ""AreasOfExpertise"", ""HighestDegree"", ""DisabilitiesIfAny"", ""Nationality"", ""PersonalWebPage"", ""SocialMediaLink""
                FROM auth.users;

                INSERT INTO auth.roles (""Id"", ""Name"", ""NormalizedName"", ""ConcurrencyStamp"") VALUES
                    ('role_admin', 'Admin', 'ADMIN', md5(random()::text)),
                    ('role_member', 'Member', 'MEMBER', md5(random()::text));

                INSERT INTO auth.user_roles (""UserId"", ""RoleId"")
                SELECT ""Id"", CASE WHEN lower(""Role"") = 'admin' THEN 'role_admin' ELSE 'role_member' END
                FROM auth.users;
            ");

            migrationBuilder.DropColumn(
                name: "VolunteeingExperience",
                schema: "auth",
                table: "users");

            migrationBuilder.DropColumn(
                name: "SocialMediaLink",
                schema: "auth",
                table: "users");

            migrationBuilder.DropColumn(
                name: "ReasonForJoining",
                schema: "auth",
                table: "users");

            migrationBuilder.DropColumn(
                name: "PresentOrganization",
                schema: "auth",
                table: "users");

            migrationBuilder.DropColumn(
                name: "IsMailingAddressSameAsPermanentAddress",
                schema: "auth",
                table: "users");

            migrationBuilder.DropIndex(
                name: "IX_users_Email",
                schema: "auth",
                table: "users");

            migrationBuilder.DropColumn(
                name: "AreasOfExpertise",
                schema: "auth",
                table: "users");

            migrationBuilder.DropColumn(
                name: "BloodGroup",
                schema: "auth",
                table: "users");

            migrationBuilder.DropColumn(
                name: "CityOfResidence",
                schema: "auth",
                table: "users");

            migrationBuilder.DropColumn(
                name: "CountryOfResidence",
                schema: "auth",
                table: "users");

            migrationBuilder.DropColumn(
                name: "DateOfBirth",
                schema: "auth",
                table: "users");

            migrationBuilder.DropColumn(
                name: "DisabilitiesIfAny",
                schema: "auth",
                table: "users");

            migrationBuilder.DropColumn(
                name: "FirstName",
                schema: "auth",
                table: "users");

            migrationBuilder.DropColumn(
                name: "Gender",
                schema: "auth",
                table: "users");

            migrationBuilder.DropColumn(
                name: "HighestDegree",
                schema: "auth",
                table: "users");

            migrationBuilder.DropColumn(
                name: "LastName",
                schema: "auth",
                table: "users");

            migrationBuilder.DropColumn(
                name: "MailingAddress",
                schema: "auth",
                table: "users");

            migrationBuilder.DropColumn(
                name: "Nationality",
                schema: "auth",
                table: "users");

            migrationBuilder.DropColumn(
                name: "Password",
                schema: "auth",
                table: "users");

            migrationBuilder.DropColumn(
                name: "PermanentAddress",
                schema: "auth",
                table: "users");

            migrationBuilder.DropColumn(
                name: "PersonalWebPage",
                schema: "auth",
                table: "users");

            migrationBuilder.DropColumn(
                name: "Role",
                schema: "auth",
                table: "users");

            migrationBuilder.AlterColumn<string>(
                name: "Email",
                schema: "auth",
                table: "users",
                type: "character varying(320)",
                maxLength: 320,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(320)",
                oldMaxLength: 320);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("One-way migration: plain-text passwords were dropped and cannot be restored (#22).");
        }
    }
}
