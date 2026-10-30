using System.Security.Claims;
using AuthIdentityService;
using AuthIdentityService.Contracts;
using AuthIdentityService.Data;
using AuthIdentityService.Data.Entities;
using AuthIdentityService.Identity;
using AuthIdentityService.Tokens;
using KJWebsite.BuildingBlocks;
using KJWebsite.BuildingBlocks.Auditing;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer((document, context, ct) =>
    {
        document.Info.Title = "Auth Identity Service API";
        document.Info.Description = "Handles user registration, login, token refresh, logout, and the authenticated user profile endpoint. " +
            "After a successful login, copy the `access_token` value and use it as the Bearer token for the `/me` endpoint.";
        document.Info.Version = "v1";
        return Task.CompletedTask;
    });
});

builder.Services.AddDbContext<AuthDbContext>((services, options) =>
{
    var configuration = services.GetRequiredService<IConfiguration>();
    var connectionString = configuration.GetConnectionString("AuthDb")
        ?? (configuration.GetValue<bool>("OpenApi:Export") ? "Host=localhost;Database=auth" : throw new InvalidOperationException("ConnectionStrings:AuthDb is required."));
    options.UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "auth"));
    options.AddInterceptors(services.GetRequiredService<AuditInterceptor>());
});
builder.Services.AddKjAuditing("auth", audit => audit
    .Audit<AuthUserEntity>("PasswordHash", "SecurityStamp", "ConcurrencyStamp")
    .Audit<AuthRoleEntity>("ConcurrencyStamp")
    .Audit<IdentityRoleClaim<string>>()
    .Audit<IdentityUserRole<string>>()
    .Audit<MemberProfileEntity>());

// Confirmation and reset tokens are protected by the data-protection key ring (see backlog: share it across replicas).
builder.Services.AddDataProtection();
builder.Services.AddIdentityCore<AuthUserEntity>(options =>
    {
        var identity = builder.Configuration.GetSection("Identity");
        options.User.RequireUniqueEmail = true;
        options.Password.RequiredLength = identity.GetValue("Password:RequiredLength", 10);
        options.Password.RequireDigit = false;
        options.Password.RequireLowercase = false;
        options.Password.RequireUppercase = false;
        options.Password.RequireNonAlphanumeric = false;
        options.Lockout.AllowedForNewUsers = true;
        options.Lockout.MaxFailedAccessAttempts = identity.GetValue("Lockout:MaxFailedAccessAttempts", 5);
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(identity.GetValue("Lockout:Minutes", 15));
    })
    .AddRoles<AuthRoleEntity>()
    .AddEntityFrameworkStores<AuthDbContext>()
    .AddDefaultTokenProviders();
builder.Services.AddSingleton(services => new InMemoryAuthEmailSender(
    services.GetRequiredService<ILogger<InMemoryAuthEmailSender>>(), builder.Environment.IsDevelopment()));
builder.Services.AddSingleton<IAuthEmailSender>(services => services.GetRequiredService<InMemoryAuthEmailSender>());

// Resolved from the final configuration (not builder.Configuration) so host-level overrides apply; the key is
// forced to load right after Build, so a missing production key stops startup instead of the first login.
builder.Services.AddSingleton(services => AuthSigningKey.Create(services.GetRequiredService<IConfiguration>(), services.GetRequiredService<IHostEnvironment>()));
builder.Services.AddSingleton(services => KjJwtSettings.From(services.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<AccessTokenIssuer>();
builder.Services.AddScoped<RefreshTokenService>();
builder.Services.AddScoped<RoleAdministration>();
builder.Services.AddKjJwtAuthentication(services => services.GetRequiredService<AuthSigningKey>().VerificationKeys, async context =>
{
    // Access tokens are stateless, but a revoked session should stop working here at once, not after 15 minutes.
    var sessions = context.HttpContext.RequestServices.GetRequiredService<RefreshTokenService>();
    if (!await sessions.IsSessionActiveAsync(context.Principal?.FindFirstValue(KjClaims.SessionId)))
    {
        context.Fail("The session has been revoked.");
    }
});

var app = builder.Build();
_ = app.Services.GetRequiredService<AuthSigningKey>();

if (!(app.Environment.IsDevelopment() && app.Configuration.GetValue<bool>("OpenApi:Export")))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
    // Tests swap in the Sqlite provider, which has no migrations to replay; Postgres keeps its migration history.
    if (db.Database.IsNpgsql())
    {
        db.Database.Migrate();
    }
    else
    {
        db.Database.EnsureCreated();
    }

    await AuthSeeder.SeedAsync(scope.ServiceProvider, app.Environment.IsDevelopment());
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
    app.MapScalarApiReference(options =>
    {
        options.WithTitle("Auth Identity Service API")
               .WithDefaultHttpClient(ScalarTarget.CSharp, ScalarClient.HttpClient)
               .AddHttpAuthentication("Bearer", bearer => { bearer.Token = string.Empty; });
    }).AllowAnonymous();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "AuthIdentityService" }))
   .WithName("AuthHealth")
   .WithSummary("Auth service health check")
   .WithTags("System")
.AllowAnonymous();

var requireConfirmedEmail = app.Configuration.GetValue("Identity:RequireConfirmedEmail", true);
const string ResetMessage = "If the account exists, an email has been sent.";

app.MapPost("/api/v1/auth/register", async (HttpContext context, LegacyRegistrationRequest payload, UserManager<AuthUserEntity> users, IAuthEmailSender mail) =>
{
    if (string.IsNullOrWhiteSpace(payload.Email) || string.IsNullOrWhiteSpace(payload.Password))
    {
        return ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", "email and password are required.");
    }

    var email = payload.Email.Trim().ToLowerInvariant();
    if (await users.FindByEmailAsync(email) is not null)
    {
        return ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", "email already exists.");
    }

    // The requested `roles` value is ignored: letting a public caller pick "admin" is a privilege escalation.
    var user = new AuthUserEntity
    {
        Id = $"usr_{Guid.NewGuid():N}",
        UserName = email,
        Email = email,
        Status = "active",
        CreatedAt = DateTimeOffset.UtcNow,
        Profile = new MemberProfileEntity
        {
            FirstName = payload.FirstName,
            LastName = payload.LastName,
            Gender = payload.Gender,
            ReasonForJoining = payload.ReasonForJoining,
            PresentOrganization = payload.PresentOrganization,
            VolunteeingExperience = payload.VolunteeingExperience,
            DateOfBirth = payload.DateOfBirth,
            CityOfResidence = payload.CityOfResidence,
            CountryOfResidence = payload.CountryOfResidence,
            PermanentAddress = payload.PermanentAddress,
            MailingAddress = payload.MailingAddress,
            IsMailingAddressSameAsPermanentAddress = payload.IsMailingAddressSameAsPermanentAddress ?? false,
            BloodGroup = payload.BloodGroup,
            AreasOfExpertise = payload.AreasOfExpertise,
            HighestDegree = payload.HighestDegree,
            DisabilitiesIfAny = payload.DisabilitiesIfAny,
            Nationality = payload.Nationality,
            PersonalWebPage = payload.PersonalWebPage,
            SocialMediaLink = payload.SocialMediaLink
        }
    };

    var created = await users.CreateAsync(user, payload.Password);
    if (!created.Succeeded)
    {
        return ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", string.Join(" ", created.Errors.Select(e => e.Description)));
    }

    await users.AddToRoleAsync(user, "Member");
    await mail.SendAsync(new AuthEmail(AuthEmailKind.ConfirmEmail, user.Id, email, await users.GenerateEmailConfirmationTokenAsync(user)));

    return Results.Created($"/api/v1/auth/users/{user.Id}", new { id = user.Id, email = user.Email, role = "Member", emailConfirmationRequired = requireConfirmedEmail });
})
.WithName("Register")
.WithSummary("Register a new user account")
.WithDescription("Creates a new member account with a hashed password and sends an email-confirmation message. The requested role is ignored: new accounts are `Member`. Includes optional legacy profile fields (name, gender, address, etc.).")
.WithTags("Auth")
.AllowAnonymous();

app.MapPost("/api/v1/auth/login", async (HttpContext context, LoginRequest payload, AuthDbContext db, UserManager<AuthUserEntity> users, RoleManager<AuthRoleEntity> roleManager, RefreshTokenService sessions, AccessTokenIssuer issuer) =>
{
    var email = payload.Email?.Trim() ?? string.Empty;
    var user = string.IsNullOrEmpty(email) ? null : await users.FindByEmailAsync(email);
    if (user is null || user.Status != "active")
    {
        // Spend the same hashing time as a real check so response timing does not reveal which emails exist.
        users.PasswordHasher.VerifyHashedPassword(new AuthUserEntity(), TimingDummyHash.Value, payload.Password ?? string.Empty);
        return ApiError.Create(context, StatusCodes.Status401Unauthorized, "UNAUTHORIZED", "Invalid credentials.");
    }

    if (await users.IsLockedOutAsync(user))
    {
        return ApiError.Create(context, StatusCodes.Status423Locked, "ACCOUNT_LOCKED", "Too many failed attempts. Try again later.");
    }

    if (!await users.CheckPasswordAsync(user, payload.Password ?? string.Empty))
    {
        await users.AccessFailedAsync(user);
        return await users.IsLockedOutAsync(user)
            ? ApiError.Create(context, StatusCodes.Status423Locked, "ACCOUNT_LOCKED", "Too many failed attempts. Try again later.")
            : ApiError.Create(context, StatusCodes.Status401Unauthorized, "UNAUTHORIZED", "Invalid credentials.");
    }

    await users.ResetAccessFailedCountAsync(user);
    if (requireConfirmedEmail && !user.EmailConfirmed)
    {
        return ApiError.Create(context, StatusCodes.Status403Forbidden, "EMAIL_NOT_CONFIRMED", "Confirm your email address before signing in.");
    }

    var (refresh, row) = sessions.StartSession(user.Id, context.Request.Headers.UserAgent.ToString());
    await db.SaveChangesAsync();

    return await TokenResponse(users, roleManager, issuer, user, row.FamilyId, refresh);
})
.WithName("Login")
.WithSummary("Login and receive tokens")
.WithDescription("Authenticates a user and returns an `access_token` (RS256 JWT, 15 min) and a `refresh_token` (opaque, stored hashed, 30 days). Repeated failures lock the account (423). Unconfirmed emails are rejected (403) unless `Identity:RequireConfirmedEmail` is off.")
.WithTags("Auth")
.AllowAnonymous();

app.MapPost("/api/v1/auth/refresh", async (HttpContext context, RefreshTokenRequest payload, UserManager<AuthUserEntity> users, RoleManager<AuthRoleEntity> roleManager, RefreshTokenService sessions, AccessTokenIssuer issuer) =>
{
    var rotation = await sessions.RotateAsync(payload.RefreshToken ?? string.Empty, context.Request.Headers.UserAgent.ToString());
    if (rotation.Outcome != RotationOutcome.Rotated)
    {
        var message = rotation.Outcome == RotationOutcome.ReplayDetected ? "Refresh token reuse detected; the session has been ended." : "Invalid refresh token.";
        return ApiError.Create(context, StatusCodes.Status401Unauthorized, "UNAUTHORIZED", message);
    }

    var user = await users.FindByIdAsync(rotation.Row!.UserId);
    if (user is null || user.Status != "active" || await users.IsLockedOutAsync(user))
    {
        await sessions.RevokeSessionAsync(rotation.Row.FamilyId);
        return ApiError.Create(context, StatusCodes.Status401Unauthorized, "UNAUTHORIZED", "Invalid refresh token.");
    }

    return await TokenResponse(users, roleManager, issuer, user, rotation.Row.FamilyId, rotation.Token!);
})
.WithName("RefreshToken")
.WithSummary("Refresh an expired access token")
.WithDescription("Rotates the refresh token and issues a new access token. The old refresh token is single-use; presenting it again ends the whole session.")
.WithTags("Auth")
.AllowAnonymous();

app.MapPost("/api/v1/auth/logout", async (HttpContext context, RefreshTokenRequest payload, RefreshTokenService sessions) =>
{
    if (!await sessions.RevokeByTokenAsync(payload.RefreshToken ?? string.Empty))
    {
        return ApiError.Create(context, StatusCodes.Status401Unauthorized, "UNAUTHORIZED", "Invalid refresh token.");
    }

    return Results.NoContent();
})
.WithName("Logout")
.WithSummary("Logout and revoke refresh token")
.WithDescription("Ends the session the given refresh token belongs to. Returns 204 No Content on success.")
.WithTags("Auth")
.AllowAnonymous();

app.MapPost("/api/v1/auth/confirm-email", async (HttpContext context, ConfirmEmailRequest payload, UserManager<AuthUserEntity> users) =>
{
    var user = string.IsNullOrWhiteSpace(payload.UserId) ? null : await users.FindByIdAsync(payload.UserId);
    if (user is null || string.IsNullOrWhiteSpace(payload.Token) || !(await users.ConfirmEmailAsync(user, payload.Token)).Succeeded)
    {
        return ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", "The confirmation link is invalid or has expired.");
    }

    return Results.NoContent();
})
.WithName("ConfirmEmail")
.WithSummary("Confirm an email address")
.WithDescription("Confirms the address using the token from the confirmation email. Returns 204 on success.")
.WithTags("Auth")
.AllowAnonymous();

app.MapPost("/api/v1/auth/resend-confirmation", async (ResendConfirmationRequest payload, UserManager<AuthUserEntity> users, IAuthEmailSender mail) =>
{
    var user = string.IsNullOrWhiteSpace(payload.Email) ? null : await users.FindByEmailAsync(payload.Email.Trim());
    if (user is { EmailConfirmed: false, Status: "active" })
    {
        await mail.SendAsync(new AuthEmail(AuthEmailKind.ConfirmEmail, user.Id, user.Email!, await users.GenerateEmailConfirmationTokenAsync(user)));
    }

    return Results.Accepted(value: new { message = ResetMessage });
})
.WithName("ResendConfirmation")
.WithSummary("Resend the confirmation email")
.WithDescription("Always returns 202, whether or not the account exists.")
.WithTags("Auth")
.AllowAnonymous();

app.MapPost("/api/v1/auth/forgot-password", async (ForgotPasswordRequest payload, UserManager<AuthUserEntity> users, IAuthEmailSender mail) =>
{
    var user = string.IsNullOrWhiteSpace(payload.Email) ? null : await users.FindByEmailAsync(payload.Email.Trim());
    if (user is { Status: "active" })
    {
        await mail.SendAsync(new AuthEmail(AuthEmailKind.ResetPassword, user.Id, user.Email!, await users.GeneratePasswordResetTokenAsync(user)));
    }

    return Results.Accepted(value: new { message = ResetMessage });
})
.WithName("ForgotPassword")
.WithSummary("Request a password-reset email")
.WithDescription("Always returns 202, whether or not the account exists, so it cannot be used to discover accounts.")
.WithTags("Auth")
.AllowAnonymous();

app.MapPost("/api/v1/auth/reset-password", async (HttpContext context, ResetPasswordRequest payload, UserManager<AuthUserEntity> users, RefreshTokenService sessions) =>
{
    var user = string.IsNullOrWhiteSpace(payload.Email) ? null : await users.FindByEmailAsync(payload.Email.Trim());
    if (user is null || user.Status != "active" || string.IsNullOrWhiteSpace(payload.Token) || string.IsNullOrEmpty(payload.NewPassword))
    {
        return ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", "The reset link is invalid or has expired.");
    }

    var result = await users.ResetPasswordAsync(user, payload.Token, payload.NewPassword);
    if (!result.Succeeded)
    {
        return ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", string.Join(" ", result.Errors.Select(e => e.Description)));
    }

    // The emailed token proves control of the mailbox, and a new password ends any lockout.
    user.EmailConfirmed = true;
    user.LockoutEnd = null;
    user.AccessFailedCount = 0;
    await users.UpdateAsync(user);

    // A password change ends every existing session (plan §7).
    await sessions.RevokeAllAsync(user.Id);
    return Results.NoContent();
})
.WithName("ResetPassword")
.WithSummary("Set a new password with a reset token")
.WithDescription("Sets the password using the emailed token, lifts any lockout and revokes all refresh tokens. Returns 204 on success.")
.WithTags("Auth")
.AllowAnonymous();

app.MapGet("/api/v1/auth/me", async (ClaimsPrincipal principal, AuthDbContext db, UserManager<AuthUserEntity> users) =>
{
    var userId = principal.FindFirstValue(KjClaims.Subject);
    var user = await db.Users.Include(u => u.Profile).FirstOrDefaultAsync(u => u.Id == userId);
    if (user is null)
    {
        return Results.Unauthorized();
    }

    var profile = user.Profile ?? new MemberProfileEntity();
    return Results.Ok(new
    {
        id = user.Id,
        email = user.Email,
        role = await RoleOf(users, user),
        profile = new
        {
            profile.FirstName,
            profile.LastName,
            profile.Gender,
            profile.ReasonForJoining,
            profile.PresentOrganization,
            profile.VolunteeingExperience,
            profile.DateOfBirth,
            profile.CityOfResidence,
            profile.CountryOfResidence,
            profile.PermanentAddress,
            profile.MailingAddress,
            profile.IsMailingAddressSameAsPermanentAddress,
            profile.BloodGroup,
            profile.AreasOfExpertise,
            profile.HighestDegree,
            profile.DisabilitiesIfAny,
            profile.Nationality,
            profile.PersonalWebPage,
            profile.SocialMediaLink
        }
    });
})
.WithName("GetCurrentUser")
.WithSummary("Get the authenticated user's profile")
.WithDescription("Returns the full profile of the currently authenticated user. Requires `Authorization: Bearer <access_token>` obtained from the login endpoint.")
.WithTags("Auth")
.RequireAuthorization();

app.MapGet("/.well-known/jwks.json", (AuthSigningKey signingKey) => Results.Json(signingKey.ToJwks()))
    .WithName("Jwks")
    .WithSummary("Public signing keys")
    .WithDescription("The public keys that verify access tokens (JSON Web Key Set). Contains no secrets.")
    .WithTags("System")
.AllowAnonymous();

app.MapGet("/api/v1/auth/sessions", async (ClaimsPrincipal principal, RefreshTokenService sessions) =>
{
    var current = principal.FindFirstValue(KjClaims.SessionId);
    var active = await sessions.ActiveSessionsAsync(principal.FindFirstValue(KjClaims.Subject)!);
    return Results.Ok(new
    {
        items = active.Select(s => new
        {
            id = s.FamilyId,
            createdAt = s.SessionCreatedAt,
            lastUsedAt = s.CreatedAt,
            userAgent = s.UserAgent,
            current = s.FamilyId == current
        })
    });
})
.WithName("ListSessions")
.WithSummary("List the signed-in user's active sessions")
.WithDescription("One entry per device/session with a live refresh token. `current` marks the session making the request.")
.WithTags("Auth")
.RequireAuthorization();

app.MapDelete("/api/v1/auth/sessions/{id}", async (HttpContext context, string id, ClaimsPrincipal principal, RefreshTokenService sessions) =>
{
    return await sessions.RevokeSessionAsync(id, principal.FindFirstValue(KjClaims.Subject))
        ? Results.NoContent()
        : ApiError.Create(context, StatusCodes.Status404NotFound, "NOT_FOUND", "Session not found.");
})
.WithName("RevokeSession")
.WithSummary("End one of the user's sessions")
.WithDescription("Revokes the session's refresh tokens. Another user's session id is reported as not found.")
.WithTags("Auth")
.RequireAuthorization();

app.MapPost("/api/v1/auth/logout-all", async (ClaimsPrincipal principal, RefreshTokenService sessions) =>
{
    await sessions.RevokeAllAsync(principal.FindFirstValue(KjClaims.Subject)!);
    return Results.NoContent();
})
.WithName("LogoutAll")
.WithSummary("End every session of the signed-in user")
.WithDescription("Revokes all refresh tokens for the user, on every device. Returns 204.")
.WithTags("Auth")
.RequireAuthorization();

static async Task<IResult> TokenResponse(UserManager<AuthUserEntity> users, RoleManager<AuthRoleEntity> roleManager, AccessTokenIssuer issuer, AuthUserEntity user, string sessionId, string refreshToken)
{
    var roles = await users.GetRolesAsync(user);
    var permissions = new List<string>();
    foreach (var name in roles)
    {
        var role = await roleManager.FindByNameAsync(name);
        if (role is not null)
        {
            permissions.AddRange((await roleManager.GetClaimsAsync(role)).Where(c => c.Type == KjClaims.Permission).Select(c => c.Value));
        }
    }

    return Results.Ok(new
    {
        access_token = issuer.Issue(user, sessionId, roles, permissions),
        refresh_token = refreshToken,
        expires_in = 900,
        token_type = "Bearer",
        user = new { id = user.Id, email = user.Email, role = roles.FirstOrDefault() ?? "Member" }
    });
}

static async Task<string> RoleOf(UserManager<AuthUserEntity> users, AuthUserEntity user) =>
    (await users.GetRolesAsync(user)).FirstOrDefault() ?? "Member";

app.MapAdminAuth();
app.MapAuditLog<AuthDbContext>("/api/v1/admin/auth/audit", "Auth");

app.Run();

namespace AuthIdentityService
{
    public sealed class AuthIdentityServiceTestEntryPoint;
}
