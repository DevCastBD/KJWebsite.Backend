using AuthIdentityService.Contracts;
using AuthIdentityService.Data;
using AuthIdentityService.Data.Entities;
using AuthIdentityService.Identity;
using KJWebsite.BuildingBlocks;
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
});

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

var app = builder.Build();

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
    app.MapOpenApi();
    app.MapScalarApiReference(options =>
    {
        options.WithTitle("Auth Identity Service API")
               .WithDefaultHttpClient(ScalarTarget.CSharp, ScalarClient.HttpClient)
               .AddHttpAuthentication("Bearer", bearer => { bearer.Token = string.Empty; });
    });
}

var accessTokens = new Dictionary<string, string>(StringComparer.Ordinal);

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "AuthIdentityService" }))
   .WithName("AuthHealth")
   .WithSummary("Auth service health check")
   .WithTags("System");

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
.WithTags("Auth");

app.MapPost("/api/v1/auth/login", async (HttpContext context, LoginRequest payload, AuthDbContext db, UserManager<AuthUserEntity> users) =>
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

    var token = $"atk_{Guid.NewGuid():N}";
    var refresh = $"rt_{Guid.NewGuid():N}";
    accessTokens[token] = user.Id;

    db.RefreshTokens.Add(new RefreshTokenEntity
    {
        Id = $"rft_{Guid.NewGuid():N}",
        Token = refresh,
        UserId = user.Id,
        CreatedAt = DateTimeOffset.UtcNow,
        ExpiresAt = DateTimeOffset.UtcNow.AddDays(30)
    });
    await db.SaveChangesAsync();

    return Results.Ok(new
    {
        access_token = token,
        refresh_token = refresh,
        expires_in = 900,
        token_type = "Bearer",
        user = new { id = user.Id, email = user.Email, role = await RoleOf(users, user) }
    });
})
.WithName("Login")
.WithSummary("Login and receive tokens")
.WithDescription("Authenticates a user and returns an `access_token` (in-memory, 15 min) and a `refresh_token` (persisted, 30 days). Repeated failures lock the account (423). Unconfirmed emails are rejected (403) unless `Identity:RequireConfirmedEmail` is off.")
.WithTags("Auth");

app.MapPost("/api/v1/auth/refresh", async (HttpContext context, RefreshTokenRequest payload, AuthDbContext db, UserManager<AuthUserEntity> users) =>
{
    // Token is unique-indexed, so this fetches at most one row; expiry is then checked in memory.
    // Npgsql could not translate DateTimeOffset.UtcNow in this query (found by the first CI run, #11).
    var refreshRow = await db.RefreshTokens.FirstOrDefaultAsync(r => r.Token == payload.RefreshToken && r.RevokedAt == null);
    if (refreshRow is null || refreshRow.ExpiresAt <= DateTimeOffset.UtcNow)
    {
        return ApiError.Create(context, StatusCodes.Status401Unauthorized, "UNAUTHORIZED", "Invalid refresh token.");
    }

    var user = await users.FindByIdAsync(refreshRow.UserId);
    if (user is null || user.Status != "active" || await users.IsLockedOutAsync(user))
    {
        return ApiError.Create(context, StatusCodes.Status401Unauthorized, "UNAUTHORIZED", "Invalid refresh token.");
    }

    refreshRow.RevokedAt = DateTimeOffset.UtcNow;

    var token = $"atk_{Guid.NewGuid():N}";
    var newRefresh = $"rt_{Guid.NewGuid():N}";
    accessTokens[token] = user.Id;

    db.RefreshTokens.Add(new RefreshTokenEntity
    {
        Id = $"rft_{Guid.NewGuid():N}",
        Token = newRefresh,
        UserId = user.Id,
        CreatedAt = DateTimeOffset.UtcNow,
        ExpiresAt = DateTimeOffset.UtcNow.AddDays(30)
    });
    await db.SaveChangesAsync();

    return Results.Ok(new
    {
        access_token = token,
        refresh_token = newRefresh,
        expires_in = 900,
        token_type = "Bearer",
        user = new { id = user.Id, email = user.Email, role = await RoleOf(users, user) }
    });
})
.WithName("RefreshToken")
.WithSummary("Refresh an expired access token")
.WithDescription("Rotates the refresh token and issues a new access token. The old refresh token is immediately revoked.")
.WithTags("Auth");

app.MapPost("/api/v1/auth/logout", async (HttpContext context, RefreshTokenRequest payload, AuthDbContext db) =>
{
    var refreshRow = await db.RefreshTokens.FirstOrDefaultAsync(r => r.Token == payload.RefreshToken && r.RevokedAt == null);
    if (refreshRow is null)
    {
        return ApiError.Create(context, StatusCodes.Status401Unauthorized, "UNAUTHORIZED", "Invalid refresh token.");
    }

    refreshRow.RevokedAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync();

    return Results.NoContent();
})
.WithName("Logout")
.WithSummary("Logout and revoke refresh token")
.WithDescription("Revokes the given refresh token. Returns 204 No Content on success.")
.WithTags("Auth");

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
.WithTags("Auth");

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
.WithTags("Auth");

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
.WithTags("Auth");

app.MapPost("/api/v1/auth/reset-password", async (HttpContext context, ResetPasswordRequest payload, AuthDbContext db, UserManager<AuthUserEntity> users) =>
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
    var now = DateTimeOffset.UtcNow;
    foreach (var row in await db.RefreshTokens.Where(r => r.UserId == user.Id && r.RevokedAt == null).ToListAsync())
    {
        row.RevokedAt = now;
    }

    await db.SaveChangesAsync();
    return Results.NoContent();
})
.WithName("ResetPassword")
.WithSummary("Set a new password with a reset token")
.WithDescription("Sets the password using the emailed token, lifts any lockout and revokes all refresh tokens. Returns 204 on success.")
.WithTags("Auth");

app.MapGet("/api/v1/auth/me", async (HttpRequest request, AuthDbContext db, UserManager<AuthUserEntity> users) =>
{
    if (!request.Headers.TryGetValue("Authorization", out var authHeader))
    {
        return ApiError.Create(request.HttpContext, StatusCodes.Status401Unauthorized, "UNAUTHORIZED", "Missing Authorization header.");
    }

    const string bearerPrefix = "Bearer ";
    var raw = authHeader.ToString();
    if (!raw.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
    {
        return ApiError.Create(request.HttpContext, StatusCodes.Status401Unauthorized, "UNAUTHORIZED", "Invalid Authorization header.");
    }

    var token = raw[bearerPrefix.Length..].Trim();
    if (!accessTokens.TryGetValue(token, out var userId))
    {
        return ApiError.Create(request.HttpContext, StatusCodes.Status401Unauthorized, "UNAUTHORIZED", "Invalid token.");
    }

    var user = await db.Users.Include(u => u.Profile).FirstOrDefaultAsync(u => u.Id == userId);
    if (user is null)
    {
        return ApiError.Create(request.HttpContext, StatusCodes.Status401Unauthorized, "UNAUTHORIZED", "Invalid token.");
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
.WithTags("Auth");

static async Task<string> RoleOf(UserManager<AuthUserEntity> users, AuthUserEntity user) =>
    (await users.GetRolesAsync(user)).FirstOrDefault() ?? "Member";

app.Run();

namespace AuthIdentityService
{
    public sealed class AuthIdentityServiceTestEntryPoint;
}
