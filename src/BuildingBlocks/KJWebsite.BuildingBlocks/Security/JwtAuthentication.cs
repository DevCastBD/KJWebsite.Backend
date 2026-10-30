using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;

namespace KJWebsite.BuildingBlocks.Security;

/// <summary>Claim names shared by the service that issues access tokens and the services that verify them.</summary>
public static class KjClaims
{
    public const string Subject = "sub";
    public const string Email = "email";
    public const string Role = "role";
    public const string Permission = "permissions";
    public const string SessionId = "sid";
}

/// <summary>
/// Access-token settings (ADR-003). Only the issuing service holds a private key; every other service
/// verifies with the public key alone, so a compromised service cannot forge tokens.
/// </summary>
public sealed record KjJwtSettings(string Issuer, string Audience, TimeSpan AccessTokenLifetime)
{
    public static KjJwtSettings From(IConfiguration configuration) => new(
        configuration["Jwt:Issuer"] ?? "kjwebsite-auth",
        configuration["Jwt:Audience"] ?? "kjwebsite-api",
        TimeSpan.FromMinutes(configuration.GetValue("Jwt:AccessTokenMinutes", 15)));
}

public static class KjJwtKeys
{
    /// <summary>
    /// Where Development keeps its generated signing key, so the Auth service (private key) and the other services (its public half)
    /// agree without configuration. Never used outside Development.
    /// </summary>
    public static string DevKeyPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KJWebsite", "dev-jwt-private.pem");

    /// <summary>The public half of the Development key, if the Auth service has created one on this machine.</summary>
    public static SecurityKey? DevPublicKey()
    {
        if (!File.Exists(DevKeyPath))
        {
            return null;
        }

        using var rsa = RSA.Create();
        rsa.ImportFromPem(File.ReadAllText(DevKeyPath));
        return new RsaSecurityKey(rsa.ExportParameters(includePrivateParameters: false)) { KeyId = KeyId(rsa) };
    }

    /// <summary>A stable id so verifiers can pick the right key after a rotation.</summary>
    public static string KeyId(RSA rsa) =>
        Base64UrlEncoder.Encode(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo()))[..16];

    public static RsaSecurityKey PublicKeyFromPem(string pem)
    {
        var rsa = RSA.Create();
        rsa.ImportFromPem(pem);
        return new RsaSecurityKey(rsa.ExportParameters(includePrivateParameters: false)) { KeyId = KeyId(rsa) };
    }

    /// <summary>`Jwt:PublicKeyPem` plus any `Jwt:RetiredPublicKeysPem:n` that must still verify during a rotation.</summary>
    public static IReadOnlyList<SecurityKey> PublicKeysFromConfiguration(IConfiguration configuration)
    {
        var keys = new List<SecurityKey>();
        var current = configuration["Jwt:PublicKeyPem"];
        if (!string.IsNullOrWhiteSpace(current))
        {
            keys.Add(PublicKeyFromPem(current));
        }

        keys.AddRange(RetiredPublicKeys(configuration));
        return keys;
    }

    public static IEnumerable<SecurityKey> RetiredPublicKeys(IConfiguration configuration) =>
        configuration.GetSection("Jwt:RetiredPublicKeysPem").GetChildren()
            .Select(c => c.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => (SecurityKey)PublicKeyFromPem(v!));
}

public static class JwtAuthenticationExtensions
{
    /// <summary>
    /// Verifies RS256 access tokens locally. Errors use the shared RFC 9457 shape. `keys` is for the issuing service,
    /// which knows its own key; verifying services leave it null and configure `Jwt:PublicKeyPem`. Settings and keys are read
    /// when the first token is validated, from the final configuration, so host-level overrides (and tests) apply.
    /// </summary>
    public static IServiceCollection AddKjJwtAuthentication(
        this IServiceCollection services,
        Func<IServiceProvider, IEnumerable<SecurityKey>>? keys = null,
        Func<TokenValidatedContext, Task>? onTokenValidated = null)
    {
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
        {
            options.MapInboundClaims = false;
            options.Events = new JwtBearerEvents
            {
                OnTokenValidated = onTokenValidated ?? (_ => Task.CompletedTask),
                OnChallenge = async context =>
                {
                    context.HandleResponse();
                    await ApiError.Create(context.HttpContext, StatusCodes.Status401Unauthorized, "UNAUTHORIZED", "Authentication is required.")
                        .ExecuteAsync(context.HttpContext);
                },
                OnForbidden = context =>
                    ApiError.Create(context.HttpContext, StatusCodes.Status403Forbidden, "FORBIDDEN", "You do not have permission to do this.")
                        .ExecuteAsync(context.HttpContext)
            };
        });
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme).Configure<IServiceProvider>((options, provider) =>
        {
            var configuration = provider.GetRequiredService<IConfiguration>();
            var settings = KjJwtSettings.From(configuration);
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidIssuer = settings.Issuer,
                ValidAudience = settings.Audience,
                IssuerSigningKeys = (keys?.Invoke(provider) ?? ConfiguredKeys(configuration, provider.GetRequiredService<IHostEnvironment>())).ToList(),
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                ValidateIssuerSigningKey = true,
                RequireSignedTokens = true,
                RequireExpirationTime = true,
                ClockSkew = TimeSpan.FromSeconds(30),
                NameClaimType = KjClaims.Subject,
                RoleClaimType = KjClaims.Role
            };
        });
        services.AddKjPermissionPolicies();
        return services;
    }

    private static IEnumerable<SecurityKey> ConfiguredKeys(IConfiguration configuration, IHostEnvironment environment)
    {
        var keys = KjJwtKeys.PublicKeysFromConfiguration(configuration);
        if (keys.Count == 0 && environment.IsDevelopment() && KjJwtKeys.DevPublicKey() is { } devKey)
        {
            return [devKey];
        }

        return keys;
    }
}
