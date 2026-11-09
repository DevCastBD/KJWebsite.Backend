using System.Data;
using AuthIdentityService.Data;
using AuthIdentityService.Data.Entities;
using AuthIdentityService.Tokens;
using KJWebsite.BuildingBlocks;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AuthIdentityService;

/// <summary>`DonorId` is the Finance donor's id; `Password` is the initial password staff choose (#209 D6) and is never returned or logged.</summary>
public sealed record ProvisionDonorRequest(string? DonorId, string? Email, string? Password);

public sealed record DonorPasswordRequest(string? Password);

public sealed record DonorAccountStatusRequest(string? Status);

/// <param name="Created">True when a new account was made.</param>
/// <param name="LinkedToMember">True when the email already belonged to a member account and the donor role was added to it.</param>
public sealed record ProvisionedDonor(string UserId, bool Created, bool LinkedToMember, bool MustChangePassword);

/// <summary>
/// Sign-in accounts for donors (BE-103). Called by Finance with the staff member's token, the way Membership calls the member
/// provisioning endpoints. One person can be a member and a donor on one account (#209 D7); such a shared account is never touched
/// by the donor password and status endpoints, because disabling or re-passwording a member belongs to membership staff.
/// </summary>
public static class DonorProvisioningEndpoints
{
    /// <summary>Marks an account made for a donor, the way an application id marks a member account. Keeps provisioning idempotent.</summary>
    public const string ProvisionedForPrefix = "donor:";

    /// <summary>The existing OpenAPI group of the account endpoints; the escape keeps the exact same name without typing the character.</summary>
    private const string AdminTag = "Admin \u2014 Roles and users";

    public static bool IsDonorProvisioned(this AuthUserEntity user) =>
        user.ProvisionedFor?.StartsWith(ProvisionedForPrefix, StringComparison.Ordinal) == true;

    public static void MapDonorProvisioning(this WebApplication app)
    {
        app.MapPost("/api/v1/admin/auth/donors", async (HttpContext context, ProvisionDonorRequest payload, AuthDbContext db, UserManager<AuthUserEntity> users) =>
        {
            var invalid = new List<string>();
            if (string.IsNullOrWhiteSpace(payload.DonorId) || payload.DonorId.Length > 56)
            {
                invalid.Add("donorId");
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
            var marker = ProvisionedForPrefix + payload.DonorId!.Trim();

            // Retrying finds the account made by the first attempt. It never resets the password: that is its own endpoint.
            var made = await users.Users.FirstOrDefaultAsync(u => u.ProvisionedFor == marker);
            if (made is not null)
            {
                return Results.Ok(new ProvisionedDonor(made.Id, false, false, made.MustChangePassword));
            }

            var existing = await users.FindByEmailAsync(email);
            if (existing is not null)
            {
                // One person, one sign-in (D7): a member who becomes a donor keeps their password, sessions and account; they only gain the role.
                if (!await users.IsInRoleAsync(existing, KjRoles.Member))
                {
                    return ApiError.Create(context, StatusCodes.Status409Conflict, "EMAIL_TAKEN", "An account with this email already exists.");
                }

                if (!await users.IsInRoleAsync(existing, KjRoles.Donor))
                {
                    var added = await users.AddToRoleAsync(existing, KjRoles.Donor);
                    if (!added.Succeeded)
                    {
                        return ApiError.Create(context, StatusCodes.Status500InternalServerError, "PROVISIONING_FAILED", "The Donor role could not be assigned.");
                    }
                }

                return Results.Ok(new ProvisionedDonor(existing.Id, false, true, existing.MustChangePassword));
            }

            if (string.IsNullOrEmpty(payload.Password))
            {
                return ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", "Invalid fields: password.");
            }

            // User and role land together or not at all: an account with no role is the half-provisioned state to avoid.
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            var user = new AuthUserEntity
            {
                Id = $"usr_{Guid.NewGuid():N}",
                UserName = email,
                Email = email,
                EmailConfirmed = true,
                Status = "active",
                CreatedAt = DateTimeOffset.UtcNow,
                ProvisionedFor = marker,
                MustChangePassword = true
            };

            var created = await users.CreateAsync(user, payload.Password);
            if (!created.Succeeded)
            {
                // Identity's messages describe the rule that failed ("at least 10 characters"), never the value.
                return ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", $"Invalid fields: password. {string.Join(" ", created.Errors.Select(e => e.Description))}");
            }

            var role = await users.AddToRoleAsync(user, KjRoles.Donor);
            if (!role.Succeeded)
            {
                return ApiError.Create(context, StatusCodes.Status500InternalServerError, "PROVISIONING_FAILED", "The Donor role could not be assigned.");
            }

            await transaction.CommitAsync();
            return Results.Created($"/api/v1/admin/auth/donors/{user.Id}", new ProvisionedDonor(user.Id, true, false, true));
        })
        .WithName("ProvisionDonor")
        .WithSummary("Create the sign-in account for a donor")
        .WithDescription("Creates an email-confirmed Donor account with the initial password staff choose (the platform password rules apply) and the must-change flag set. Never returns the password. Idempotent per `donorId`. When the email already belongs to a member account the Donor role is added to it and nothing else changes; any other existing account gives 409 EMAIL_TAKEN.")
        .WithTags(AdminTag)
        .RequirePermission(Permissions.FinanceDonorProvision);

        // For a donor who lost access: staff choose a new initial password, which must be changed at the next sign-in.
        app.MapPut("/api/v1/admin/auth/donors/{userId}/password", async (HttpContext context, string userId, DonorPasswordRequest payload, UserManager<AuthUserEntity> users, RefreshTokenService sessions) =>
        {
            if (string.IsNullOrEmpty(payload.Password))
            {
                return ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", "Invalid fields: password.");
            }

            var user = await users.FindByIdAsync(userId);
            var refusal = RefuseUnlessDonorAccount(context, user);
            if (refusal is not null)
            {
                return refusal;
            }

            var reset = await users.ResetPasswordAsync(user!, await users.GeneratePasswordResetTokenAsync(user!), payload.Password);
            if (!reset.Succeeded)
            {
                return ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", $"Invalid fields: password. {string.Join(" ", reset.Errors.Select(e => e.Description))}");
            }

            user!.MustChangePassword = true;
            user.LockoutEnd = null;
            await users.ResetAccessFailedCountAsync(user);
            await users.UpdateAsync(user);
            await sessions.RevokeAllAsync(user.Id);
            return Results.Ok(new { userId = user.Id, mustChangePassword = true });
        })
        .WithName("SetDonorPassword")
        .WithSummary("Set a new initial password for a donor")
        .WithDescription("Sets the must-change flag again, clears a lockout and ends every session. Only accounts made for a donor: an account shared with a member returns 409 ACCOUNT_SHARED.")
        .WithTags(AdminTag)
        .RequirePermission(Permissions.FinanceDonorProvision);

        app.MapPut("/api/v1/admin/auth/donors/{userId}/status", async (HttpContext context, string userId, DonorAccountStatusRequest payload, UserManager<AuthUserEntity> users, RefreshTokenService sessions) =>
        {
            if (payload.Status is not ("active" or "disabled"))
            {
                return ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", "Invalid fields: status (active or disabled).");
            }

            var user = await users.FindByIdAsync(userId);
            var refusal = RefuseUnlessDonorAccount(context, user);
            if (refusal is not null)
            {
                return refusal;
            }

            user!.Status = payload.Status;
            var updated = await users.UpdateAsync(user);
            if (!updated.Succeeded)
            {
                return ApiError.Create(context, StatusCodes.Status500InternalServerError, "UPDATE_FAILED", "The account could not be updated.");
            }

            if (payload.Status == "disabled")
            {
                await sessions.RevokeAllAsync(user.Id);
            }

            return Results.Ok(new { userId = user.Id, status = user.Status });
        })
        .WithName("SetDonorAccountStatus")
        .WithSummary("Disable or re-enable a donor's login")
        .WithDescription("Disabling blocks sign-in and refresh at once and ends every session. Only accounts made for a donor: an account shared with a member returns 409 ACCOUNT_SHARED, because membership staff own that decision.")
        .WithTags(AdminTag)
        .RequirePermission(Permissions.FinanceDonorProvision);
    }

    /// <summary>Null when the account is one made for a donor; otherwise the refusal to return.</summary>
    private static IResult? RefuseUnlessDonorAccount(HttpContext context, AuthUserEntity? user)
    {
        if (user is null)
        {
            return ApiError.Create(context, StatusCodes.Status404NotFound, "NOT_FOUND", "Donor account not found.");
        }

        if (user.IsDonorProvisioned())
        {
            return null;
        }

        // A member who is also a donor, or any other account: staff with the donor permission must not change their password or switch them off.
        return ApiError.Create(context, StatusCodes.Status409Conflict, "ACCOUNT_SHARED", "This account is not a donor-only account. Membership staff manage it.");
    }
}
