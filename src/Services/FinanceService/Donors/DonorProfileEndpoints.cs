using System.Globalization;
using System.Text.RegularExpressions;
using FinanceService.Data;
using FinanceService.Data.Entities;
using FinanceService.Donations;
using FinanceService.Receipts;
using KJWebsite.BuildingBlocks;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.EntityFrameworkCore;

namespace FinanceService.Donors;

/// <summary>
/// A donor profile as staff send it. On create `Name` and `Email` are required; on update a field left out stays as it is, and an
/// optional text field sent as an empty string is cleared.
/// </summary>
public sealed record DonorProfileRequest(
    string? Name, string? Email, string? Phone, string? Language, string? Type, string? Organisation,
    string? Address, string? Country, string? Notes, string? Status);

/// <summary>The initial password staff choose for a donor's sign-in. Write-only: no response ever carries it.</summary>
public sealed record DonorLoginRequest(string? Password);

public sealed record DonorLoginStatusRequest(string? Status);

/// <param name="MustChangePassword">Read from Identity just now; null when Identity could not answer.</param>
public sealed record DonorLoginView(string DonorId, string AccountStatus, bool AccountShared, bool? MustChangePassword);

/// <summary>
/// Create and edit donor profiles and give a donor a sign-in (BE-104, #211). A donor is identified by the key it was created with and never
/// by its current email, so an email can be corrected without losing the giving history; the email only decides which donor a new gift joins.
/// </summary>
public static partial class DonorProfileEndpoints
{
    private const string Route = "/api/v1/admin/finance/donors";

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"^\+[1-9]\d{6,14}$")]
    private static partial Regex PhonePattern();

    [GeneratedRegex(@"^[A-Z]{2}$")]
    private static partial Regex CountryPattern();

    public static void MapDonorProfiles(this IEndpointRouteBuilder app)
    {
        app.MapPost(Route, async (HttpContext context, DonorProfileRequest payload, FinanceDbContext db, IDonorProtector protector, DonorKeyHasher keys, DonorData data, IAuthDonorClient auth, TimeProvider clock) =>
        {
            var invalid = Validate(payload, creating: true);
            var type = Clean(payload.Type) ?? DonorTypes.Individual;
            if (invalid.Count == 0 && type == DonorTypes.Organisation && string.IsNullOrWhiteSpace(payload.Organisation))
            {
                invalid.Add("organisation");
            }

            if (invalid.Count > 0)
            {
                return Invalid(context, invalid);
            }

            var email = payload.Email!.Trim();
            var emailKey = keys.ForEmail(email);
            var existing = await db.Donors.AsNoTracking().Where(d => d.EmailKey == emailKey).Select(d => d.Id).FirstOrDefaultAsync(context.RequestAborted);
            if (existing is not null)
            {
                return ApiError.Create(context, StatusCodes.Status409Conflict, "DONOR_EXISTS", $"A donor with this email already exists ({existing}).");
            }

            var id = PublicDonationEndpoints.NewId("dnr");
            var donor = new DonorEntity
            {
                Id = id,
                // The key a gift from this address would have made, unless an earlier donor already holds it (their email was changed later): never two donors on one key.
                DonorKey = await db.Donors.AnyAsync(d => d.DonorKey == emailKey, context.RequestAborted) ? keys.Hash($"donor:{id}") : emailKey,
                EmailKey = emailKey,
                NameProtected = protector.Protect(payload.Name!.Trim()),
                EmailProtected = protector.Protect(email),
                PhoneProtected = Optional(payload.Phone) is { } phone ? protector.Protect(phone) : null,
                Language = Clean(payload.Language) ?? "bn",
                Type = type,
                Organisation = Optional(payload.Organisation),
                AddressProtected = Optional(payload.Address) is { } address ? protector.Protect(address) : null,
                Country = Optional(payload.Country),
                NotesProtected = Optional(payload.Notes) is { } notes ? protector.Protect(notes) : null,
                CreatedAt = clock.GetUtcNow()
            };
            db.Donors.Add(donor);
            try
            {
                await db.SaveChangesAsync(context.RequestAborted);
            }
            catch (DbUpdateException)
            {
                // Two staff creating the same donor at once: the unique index on the email key lets one win.
                return ApiError.Create(context, StatusCodes.Status409Conflict, "DONOR_EXISTS", "A donor with this email already exists.");
            }

            return await DetailAsync(context, data, auth, donor, created: true);
        })
        .WithName("CreateDonor")
        .WithSummary("Create a donor profile")
        .WithDescription("Name and email are required. The email is matched to later gifts, so a gift from it joins this donor. 409 DONOR_EXISTS when a donor already has the email. Address and notes are stored encrypted. Returns the donor with an `ETag`.")
        .WithTags("Donors")
        .RequirePermission(Permissions.FinanceDonorManage);

        app.MapPatch($"{Route}/{{id}}", async (HttpContext context, string id, DonorProfileRequest payload, FinanceDbContext db, IDonorProtector protector, DonorKeyHasher keys, DonorData data, IAuthDonorClient auth) =>
        {
            var donor = await db.Donors.FirstOrDefaultAsync(d => d.Id == id, context.RequestAborted);
            if (donor is null)
            {
                return ApiError.Create(context, StatusCodes.Status404NotFound, "NOT_FOUND", "Donor not found.");
            }

            var precondition = CheckIfMatch(context, donor);
            if (precondition is not null)
            {
                return precondition;
            }

            var invalid = Validate(payload, creating: false);
            var type = Clean(payload.Type) ?? donor.Type;
            var organisation = payload.Organisation is null ? donor.Organisation : Optional(payload.Organisation);
            if (invalid.Count == 0 && type == DonorTypes.Organisation && string.IsNullOrWhiteSpace(organisation))
            {
                invalid.Add("organisation");
            }

            if (invalid.Count > 0)
            {
                return Invalid(context, invalid);
            }

            if (payload.Email is not null && !string.Equals(payload.Email.Trim(), protector.Unprotect(donor.EmailProtected), StringComparison.OrdinalIgnoreCase))
            {
                // The sign-in email lives in Identity, so it cannot change under a donor who has one.
                if (donor.AccountSubject is not null)
                {
                    return ApiError.Create(context, StatusCodes.Status409Conflict, "EMAIL_HAS_ACCOUNT", "This donor has a sign-in account, so the email cannot be changed here.");
                }

                var newKey = keys.ForEmail(payload.Email.Trim());
                if (await db.Donors.AnyAsync(d => d.EmailKey == newKey && d.Id != donor.Id, context.RequestAborted))
                {
                    return ApiError.Create(context, StatusCodes.Status409Conflict, "DONOR_EXISTS", "Another donor already has this email.");
                }

                donor.EmailKey = newKey;
                donor.EmailProtected = protector.Protect(payload.Email.Trim());
            }

            // Setting a donor inactive switches off a donor-only sign-in first, so a failure saves nothing. A sign-in shared with a member is left alone:
            // the donor endpoints refuse an inactive donor, and the member keeps their access (membership staff own that decision).
            if (payload.Status == DonorStatuses.Inactive && donor.Status != DonorStatuses.Inactive
                && donor.AccountSubject is not null && !donor.AccountShared && donor.AccountStatus != AccountStatuses.Disabled)
            {
                var switched = await auth.SetStatusAsync(donor.AccountSubject, enabled: false, Bearer(context), context.RequestAborted);
                if (!switched.Succeeded)
                {
                    return AuthFailure(context, switched);
                }

                donor.AccountStatus = AccountStatuses.Disabled;
            }

            if (payload.Name is not null)
            {
                donor.NameProtected = protector.Protect(payload.Name.Trim());
            }

            if (payload.Phone is not null)
            {
                donor.PhoneProtected = Optional(payload.Phone) is { } phone ? protector.Protect(phone) : null;
            }

            if (payload.Address is not null)
            {
                donor.AddressProtected = Optional(payload.Address) is { } address ? protector.Protect(address) : null;
            }

            if (payload.Notes is not null)
            {
                donor.NotesProtected = Optional(payload.Notes) is { } notes ? protector.Protect(notes) : null;
            }

            donor.Language = Clean(payload.Language) ?? donor.Language;
            donor.Type = type;
            donor.Organisation = organisation;
            donor.Country = payload.Country is null ? donor.Country : Optional(payload.Country);
            donor.Status = Clean(payload.Status) ?? donor.Status;
            donor.Version++;

            try
            {
                await db.SaveChangesAsync(context.RequestAborted);
            }
            catch (DbUpdateConcurrencyException)
            {
                return ApiError.Create(context, StatusCodes.Status412PreconditionFailed, "PRECONDITION_FAILED", "The donor was changed by someone else. Reload it and apply your edit again.");
            }

            return await DetailAsync(context, data, auth, donor, created: false);
        })
        .WithName("UpdateDonor")
        .WithSummary("Update a donor profile")
        .WithDescription("`If-Match` with the donor's `ETag` is required: 428 without it, 412 when the donor changed since. Fields left out stay as they are. The email can change unless the donor has a sign-in account; gifts and history stay with the donor, because it is identified by its own key and not by the email. Setting `status` to inactive switches off a donor-only sign-in; a sign-in shared with a member is not touched.")
        .WithTags("Donors")
        .RequirePermission(Permissions.FinanceDonorManage);

        app.MapPost($"{Route}/{{id}}/login", async (HttpContext context, string id, DonorLoginRequest payload, FinanceDbContext db, IDonorProtector protector, IAuthDonorClient auth) =>
        {
            if (string.IsNullOrEmpty(payload.Password))
            {
                return Invalid(context, ["password"]);
            }

            var donor = await db.Donors.FirstOrDefaultAsync(d => d.Id == id, context.RequestAborted);
            if (donor is null)
            {
                return ApiError.Create(context, StatusCodes.Status404NotFound, "NOT_FOUND", "Donor not found.");
            }

            if (donor.AccountSubject is not null)
            {
                return ApiError.Create(context, StatusCodes.Status409Conflict, "ACCOUNT_EXISTS", "This donor already has a sign-in account.");
            }

            if (donor.Status == DonorStatuses.Inactive)
            {
                return ApiError.Create(context, StatusCodes.Status409Conflict, "DONOR_INACTIVE", "An inactive donor cannot be given a sign-in.");
            }

            var result = await auth.ProvisionAsync(donor.Id, protector.Unprotect(donor.EmailProtected), payload.Password, Bearer(context), context.RequestAborted);
            if (!result.Succeeded || result.UserId is null)
            {
                return AuthFailure(context, result);
            }

            donor.AccountSubject = result.UserId;
            donor.AccountShared = result.LinkedToMember;
            donor.AccountStatus = AccountStatuses.Active;
            donor.Version++;
            await db.SaveChangesAsync(context.RequestAborted);
            context.Response.Headers.ETag = ETagOf(donor);
            var live = await auth.GetAsync(donor.AccountSubject, Bearer(context), context.RequestAborted);
            return Results.Json(new DonorLoginView(donor.Id, donor.AccountStatus, donor.AccountShared, live?.MustChangePassword), statusCode: result.Outcome == AuthOutcome.Created ? StatusCodes.Status201Created : StatusCodes.Status200OK);
        })
        .WithName("CreateDonorLogin")
        .WithSummary("Give a donor a sign-in account")
        .WithDescription("Staff choose the initial password; the donor must change it at the first sign-in. The sign-in name is the donor's email. When the email already belongs to a member the Donor role is added to that account and `accountShared` is true. Never returns or logs the password. 409 ACCOUNT_EXISTS when the donor already has one; 503 AUTH_UNAVAILABLE when Identity cannot be reached (nothing is saved).")
        .WithTags("Donors")
        .RequirePermission(Permissions.FinanceDonorProvision);

        app.MapPut($"{Route}/{{id}}/login/password", async (HttpContext context, string id, DonorLoginRequest payload, FinanceDbContext db, IAuthDonorClient auth) =>
        {
            if (string.IsNullOrEmpty(payload.Password))
            {
                return Invalid(context, ["password"]);
            }

            var (donor, problem) = await LoginOf(context, db, id);
            if (donor is null)
            {
                return problem!;
            }

            var result = await auth.SetPasswordAsync(donor.AccountSubject!, payload.Password, Bearer(context), context.RequestAborted);
            if (!result.Succeeded)
            {
                return AuthFailure(context, result);
            }

            return Results.Ok(new DonorLoginView(donor.Id, donor.AccountStatus, donor.AccountShared, true));
        })
        .WithName("SetDonorLoginPassword")
        .WithSummary("Set a new initial password for a donor")
        .WithDescription("For a donor who lost access. The donor must change it at the next sign-in and every session ends. 409 ACCOUNT_SHARED for a sign-in shared with a member.")
        .WithTags("Donors")
        .RequirePermission(Permissions.FinanceDonorProvision);

        app.MapPut($"{Route}/{{id}}/login/status", async (HttpContext context, string id, DonorLoginStatusRequest payload, FinanceDbContext db, IAuthDonorClient auth) =>
        {
            if (payload.Status is not ("active" or "disabled"))
            {
                return Invalid(context, ["status (active or disabled)"]);
            }

            var (donor, problem) = await LoginOf(context, db, id);
            if (donor is null)
            {
                return problem!;
            }

            if (payload.Status == "active" && donor.Status == DonorStatuses.Inactive)
            {
                return ApiError.Create(context, StatusCodes.Status409Conflict, "DONOR_INACTIVE", "Make the donor active first.");
            }

            var result = await auth.SetStatusAsync(donor.AccountSubject!, payload.Status == "active", Bearer(context), context.RequestAborted);
            if (!result.Succeeded)
            {
                return AuthFailure(context, result);
            }

            donor.AccountStatus = payload.Status;
            donor.Version++;
            await db.SaveChangesAsync(context.RequestAborted);
            context.Response.Headers.ETag = ETagOf(donor);
            return Results.Ok(new DonorLoginView(donor.Id, donor.AccountStatus, donor.AccountShared, null));
        })
        .WithName("SetDonorLoginStatus")
        .WithSummary("Disable or re-enable a donor's sign-in")
        .WithDescription("Disabling ends every session at once. 409 ACCOUNT_SHARED for a sign-in shared with a member; 409 DONOR_INACTIVE when re-enabling for an inactive donor.")
        .WithTags("Donors")
        .RequirePermission(Permissions.FinanceDonorProvision);
    }

    /// <summary>The donor with a sign-in that staff may change by password or status, or the problem to return.</summary>
    private static async Task<(DonorEntity? Donor, IResult? Problem)> LoginOf(HttpContext context, FinanceDbContext db, string id)
    {
        var donor = await db.Donors.FirstOrDefaultAsync(d => d.Id == id, context.RequestAborted);
        if (donor is null)
        {
            return (null, ApiError.Create(context, StatusCodes.Status404NotFound, "NOT_FOUND", "Donor not found."));
        }

        if (donor.AccountSubject is null)
        {
            return (null, ApiError.Create(context, StatusCodes.Status409Conflict, "NO_ACCOUNT", "This donor has no sign-in account."));
        }

        // A sign-in shared with a member is the member's: its password and its state belong to membership staff.
        return donor.AccountShared
            ? (null, ApiError.Create(context, StatusCodes.Status409Conflict, "ACCOUNT_SHARED", "This sign-in is also a member account. Membership staff manage it."))
            : (donor, null);
    }

    private static async Task<IResult> DetailAsync(HttpContext context, DonorData data, IAuthDonorClient auth, DonorEntity donor, bool created)
    {
        var view = await data.ViewOfAsync(donor, context.RequestAborted);
        var live = donor.AccountSubject is null ? null : await auth.GetAsync(donor.AccountSubject, Bearer(context), context.RequestAborted);
        context.Response.Headers.ETag = ETagOf(donor);
        var detail = await data.DetailAsync(view, live, context.RequestAborted);
        return created ? Results.Created($"{Route}/{donor.Id}", detail) : Results.Ok(detail);
    }

    internal static string ETagOf(DonorEntity donor) => $"\"{donor.Version.ToString(CultureInfo.InvariantCulture)}\"";

    internal static string? Bearer(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim() : null;
    }

    private static IResult? CheckIfMatch(HttpContext context, DonorEntity donor)
    {
        var header = context.Request.Headers.IfMatch.ToString().Trim();
        if (header.Length == 0)
        {
            return ApiError.Create(context, StatusCodes.Status428PreconditionRequired, "PRECONDITION_REQUIRED", "Send If-Match with the donor's ETag.");
        }

        var presented = (header.StartsWith("W/", StringComparison.Ordinal) ? header[2..] : header).Trim('"');
        return presented == donor.Version.ToString(CultureInfo.InvariantCulture)
            ? null
            : ApiError.Create(context, StatusCodes.Status412PreconditionFailed, "PRECONDITION_FAILED", "The donor was changed by someone else. Reload it and apply your edit again.");
    }

    private static IResult AuthFailure(HttpContext context, DonorAccountResult result) => result.Outcome switch
    {
        AuthOutcome.Invalid => ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", result.Message ?? "Invalid fields: password."),
        AuthOutcome.EmailTaken => ApiError.Create(context, StatusCodes.Status409Conflict, "EMAIL_TAKEN", "An account with this email already exists."),
        AuthOutcome.Shared => ApiError.Create(context, StatusCodes.Status409Conflict, "ACCOUNT_SHARED", "This sign-in is also a member account. Membership staff manage it."),
        AuthOutcome.NotFound => ApiError.Create(context, StatusCodes.Status404NotFound, "NOT_FOUND", "The sign-in account was not found."),
        AuthOutcome.Forbidden => ApiError.Create(context, StatusCodes.Status403Forbidden, "FORBIDDEN", "You do not have permission to do this."),
        _ => ApiError.Create(context, StatusCodes.Status503ServiceUnavailable, "AUTH_UNAVAILABLE", "The sign-in service could not be reached. Nothing was changed.")
    };

    private static IResult Invalid(HttpContext context, IEnumerable<string> fields) =>
        ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", $"Invalid fields: {string.Join(", ", fields)}.");

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>A text field to store: trimmed, and null when empty.</summary>
    private static string? Optional(string? value) => Clean(value);

    private static List<string> Validate(DonorProfileRequest p, bool creating)
    {
        var invalid = new List<string>();
        if (creating ? string.IsNullOrWhiteSpace(p.Name) : p.Name is not null && string.IsNullOrWhiteSpace(p.Name))
        {
            invalid.Add("name");
        }
        else if (p.Name is not null && p.Name.Trim().Length > 200)
        {
            invalid.Add("name");
        }

        if (creating ? string.IsNullOrWhiteSpace(p.Email) : p.Email is not null && string.IsNullOrWhiteSpace(p.Email))
        {
            invalid.Add("email");
        }
        else if (p.Email is not null && (p.Email.Trim().Length > 254 || !EmailPattern().IsMatch(p.Email.Trim())))
        {
            invalid.Add("email");
        }

        if (!string.IsNullOrWhiteSpace(p.Phone) && !PhonePattern().IsMatch(p.Phone.Trim()))
        {
            invalid.Add("phone");
        }

        if (p.Language is not null && p.Language.Trim() is not ("bn" or "en"))
        {
            invalid.Add("language");
        }

        if (p.Type is not null && !DonorTypes.All.Contains(p.Type.Trim()))
        {
            invalid.Add("type");
        }

        if (p.Status is not null && (creating || !DonorStatuses.All.Contains(p.Status.Trim())))
        {
            invalid.Add("status");
        }

        if (p.Organisation is not null && p.Organisation.Trim().Length > 200)
        {
            invalid.Add("organisation");
        }

        if (p.Address is not null && p.Address.Trim().Length > 500)
        {
            invalid.Add("address");
        }

        if (p.Notes is not null && p.Notes.Trim().Length > 2000)
        {
            invalid.Add("notes");
        }

        if (!string.IsNullOrWhiteSpace(p.Country) && !CountryPattern().IsMatch(p.Country.Trim()))
        {
            invalid.Add("country");
        }

        return invalid;
    }
}
