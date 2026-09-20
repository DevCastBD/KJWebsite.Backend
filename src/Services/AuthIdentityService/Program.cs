using AuthIdentityService.Contracts;
using AuthIdentityService.Data;
using AuthIdentityService.Data.Entities;
using KJWebsite.BuildingBlocks;
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

builder.Services.AddDbContext<AuthDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("AuthDb") ?? "Data Source=auth.db"));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
    db.Database.Migrate();

    if (!db.Users.Any(u => u.Email == "admin@site.org"))
    {
        db.Users.Add(new AuthUserEntity
        {
            Id = "usr_admin",
            Email = "admin@site.org",
            Password = "admin123",
            Role = "admin",
            Status = "active",
            CreatedAt = DateTimeOffset.UtcNow
        });
        db.SaveChanges();
    }
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

app.MapPost("/api/v1/auth/register", async (HttpContext context, LegacyRegistrationRequest payload, AuthDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(payload.Email) || string.IsNullOrWhiteSpace(payload.Password))
    {
        return ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", "email and password are required.");
    }

    var email = payload.Email.Trim().ToLowerInvariant();
    if (await db.Users.AnyAsync(u => u.Email == email))
    {
        return ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", "email already exists.");
    }

    var role = string.IsNullOrWhiteSpace(payload.Roles) ? "editor" : payload.Roles.Split(',')[0].Trim().ToLowerInvariant();
    if (role is not ("admin" or "editor")) role = "editor";

    var user = new AuthUserEntity
    {
        Id = $"usr_{Guid.NewGuid():N}",
        Email = email,
        Password = payload.Password,
        Role = role,
        Status = "active",
        CreatedAt = DateTimeOffset.UtcNow,
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
    };

    db.Users.Add(user);
    await db.SaveChangesAsync();

    return Results.Created($"/api/v1/auth/users/{user.Id}", new { id = user.Id, email = user.Email, role = user.Role });
})
.WithName("Register")
.WithSummary("Register a new user account")
.WithDescription("Creates a new user. Role defaults to 'editor'. Includes optional legacy profile fields (name, gender, address, etc.).")
.WithTags("Auth");

app.MapPost("/api/v1/auth/login", async (HttpContext context, LoginRequest payload, AuthDbContext db) =>
{
    var email = payload.Email.Trim().ToLowerInvariant();
    var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email && u.Password == payload.Password && u.Status == "active");
    if (user is null)
    {
        return ApiError.Create(context, StatusCodes.Status401Unauthorized, "UNAUTHORIZED", "Invalid credentials.");
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
        user = new { id = user.Id, email = user.Email, role = user.Role }
    });
})
.WithName("Login")
.WithSummary("Login and receive tokens")
.WithDescription("Authenticates a user and returns an `access_token` (in-memory, 15 min) and a `refresh_token` (persisted, 30 days). Dev seed: `admin@site.org` / `admin123`.")
.WithTags("Auth");

app.MapPost("/api/v1/auth/refresh", async (HttpContext context, RefreshTokenRequest payload, AuthDbContext db) =>
{
    var refreshRow = await db.RefreshTokens.FirstOrDefaultAsync(r => r.Token == payload.RefreshToken && r.RevokedAt == null && r.ExpiresAt > DateTimeOffset.UtcNow);
    if (refreshRow is null)
    {
        return ApiError.Create(context, StatusCodes.Status401Unauthorized, "UNAUTHORIZED", "Invalid refresh token.");
    }

    var user = await db.Users.FirstOrDefaultAsync(u => u.Id == refreshRow.UserId && u.Status == "active");
    if (user is null)
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
        user = new { id = user.Id, email = user.Email, role = user.Role }
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

app.MapGet("/api/v1/auth/me", async (HttpRequest request, AuthDbContext db) =>
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

    var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId);
    if (user is null)
    {
        return ApiError.Create(request.HttpContext, StatusCodes.Status401Unauthorized, "UNAUTHORIZED", "Invalid token.");
    }

    return Results.Ok(new
    {
        id = user.Id,
        email = user.Email,
        role = user.Role,
        profile = new
        {
            user.FirstName,
            user.LastName,
            user.Gender,
            user.ReasonForJoining,
            user.PresentOrganization,
            user.VolunteeingExperience,
            user.DateOfBirth,
            user.CityOfResidence,
            user.CountryOfResidence,
            user.PermanentAddress,
            user.MailingAddress,
            user.IsMailingAddressSameAsPermanentAddress,
            user.BloodGroup,
            user.AreasOfExpertise,
            user.HighestDegree,
            user.DisabilitiesIfAny,
            user.Nationality,
            user.PersonalWebPage,
            user.SocialMediaLink
        }
    });
})
.WithName("GetCurrentUser")
.WithSummary("Get the authenticated user's profile")
.WithDescription("Returns the full profile of the currently authenticated user. Requires `Authorization: Bearer <access_token>` obtained from the login endpoint.")
.WithTags("Auth");

app.Run();
