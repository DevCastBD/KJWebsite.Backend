using System.Data;
using AuthIdentityService.Data;
using AuthIdentityService.Data.Entities;
using KJWebsite.BuildingBlocks;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AuthIdentityService;

public sealed record ProvisionMemberRequest(
    string? ApplicationId,
    string? Email,
    string? FirstName,
    string? LastName,
    string? Gender,
    string? DateOfBirth,
    string? BloodGroup,
    string? Address,
    string? CityOfResidence,
    string? CountryOfResidence,
    string? ReasonForJoining,
    string? PresentOrganization,
    string? VolunteeringExperience);

/// <summary>`SetupToken` lets the new member choose a password (POST /api/v1/auth/reset-password with `UserId`).</summary>
public sealed record ProvisionedMember(string UserId, string SetupToken, bool Created);

/// <summary>Creates the login for an approved membership application (BE-045). Called by the Membership service with the approver's token.</summary>
public static class MemberProvisioningEndpoints
{
    public static void MapMemberProvisioning(this WebApplication app)
    {
        app.MapPost("/api/v1/admin/auth/members", async (HttpContext context, ProvisionMemberRequest payload, AuthDbContext db, UserManager<AuthUserEntity> users) =>
        {
            var invalid = new List<string>();
            if (string.IsNullOrWhiteSpace(payload.ApplicationId) || payload.ApplicationId.Length > 64)
            {
                invalid.Add("applicationId");
            }

            if (string.IsNullOrWhiteSpace(payload.Email) || !payload.Email.Contains('@') || payload.Email.Length > 254)
            {
                invalid.Add("email");
            }

            if (invalid.Count > 0)
            {
                return ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", $"Invalid fields: {string.Join(", ", invalid)}.");
            }

            var email = payload.Email!.Trim().ToLowerInvariant();

            // Retrying an approval finds the account made by the first attempt instead of failing on its own email.
            var existing = await users.Users.FirstOrDefaultAsync(u => u.ProvisionedFor == payload.ApplicationId);
            if (existing is not null)
            {
                return Results.Ok(new ProvisionedMember(existing.Id, await users.GeneratePasswordResetTokenAsync(existing), false));
            }

            if (await users.FindByEmailAsync(email) is not null)
            {
                return ApiError.Create(context, StatusCodes.Status409Conflict, "EMAIL_TAKEN", "An account with this email already exists.");
            }

            // User, profile and role land together or not at all: a login with no role (or the reverse) is the half-provisioned state to avoid.
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            var user = new AuthUserEntity
            {
                Id = $"usr_{Guid.NewGuid():N}",
                UserName = email,
                Email = email,
                // The organisation vetted this applicant and the setup link goes to this address; with no password set, nobody can sign in until its owner chooses one.
                EmailConfirmed = true,
                Status = "active",
                CreatedAt = DateTimeOffset.UtcNow,
                ProvisionedFor = payload.ApplicationId,
                Profile = new MemberProfileEntity
                {
                    FirstName = payload.FirstName,
                    LastName = payload.LastName,
                    Gender = payload.Gender,
                    DateOfBirth = payload.DateOfBirth,
                    BloodGroup = payload.BloodGroup,
                    PermanentAddress = payload.Address,
                    CityOfResidence = payload.CityOfResidence,
                    CountryOfResidence = payload.CountryOfResidence,
                    ReasonForJoining = payload.ReasonForJoining,
                    PresentOrganization = payload.PresentOrganization,
                    VolunteeingExperience = payload.VolunteeringExperience
                }
            };

            var created = await users.CreateAsync(user);
            if (!created.Succeeded)
            {
                return ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", string.Join(" ", created.Errors.Select(e => e.Description)));
            }

            var role = await users.AddToRoleAsync(user, KjRoles.Member);
            if (!role.Succeeded)
            {
                return ApiError.Create(context, StatusCodes.Status500InternalServerError, "PROVISIONING_FAILED", "The Member role could not be assigned.");
            }

            var token = await users.GeneratePasswordResetTokenAsync(user);
            await transaction.CommitAsync();
            return Results.Created($"/api/v1/admin/auth/members/{user.Id}", new ProvisionedMember(user.Id, token, true));
        })
        .WithName("ProvisionMember")
        .WithSummary("Create the login for an approved membership application")
        .WithDescription("Creates a password-less, email-confirmed Member account with the applicant's profile and returns a one-time setup token. Idempotent per `applicationId`: a repeat returns the same account with a fresh token. 409 EMAIL_TAKEN when another account already uses the email.")
        .WithTags("Admin — Roles and users")
        .RequirePermission(Permissions.MembershipProvision);
    }
}
