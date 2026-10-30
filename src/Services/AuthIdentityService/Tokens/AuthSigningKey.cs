using System.Security.Cryptography;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.IdentityModel.Tokens;

namespace AuthIdentityService.Tokens;

/// <summary>
/// The RS256 signing key. Outside Development it must come from `Jwt:PrivateKeyPem` (environment or a vault);
/// Development generates one and keeps it in the user's local app-data folder so sessions survive restarts.
/// </summary>
public sealed class AuthSigningKey
{
    private AuthSigningKey(RSA rsa, IReadOnlyList<SecurityKey> retired)
    {
        var keyId = KjJwtKeys.KeyId(rsa);
        SigningCredentials = new SigningCredentials(new RsaSecurityKey(rsa) { KeyId = keyId }, SecurityAlgorithms.RsaSha256);
        var publicKey = new RsaSecurityKey(rsa.ExportParameters(includePrivateParameters: false)) { KeyId = keyId };
        VerificationKeys = [publicKey, .. retired];
    }

    public SigningCredentials SigningCredentials { get; }

    /// <summary>The current public key first, then retired keys that must still verify during a rotation.</summary>
    public IReadOnlyList<SecurityKey> VerificationKeys { get; }

    public static AuthSigningKey Create(IConfiguration configuration, IHostEnvironment environment)
    {
        var retired = KjJwtKeys.RetiredPublicKeys(configuration).ToList();
        var pem = configuration["Jwt:PrivateKeyPem"];
        var rsa = RSA.Create();
        if (!string.IsNullOrWhiteSpace(pem))
        {
            rsa.ImportFromPem(pem);
        }
        else if (environment.IsDevelopment())
        {
            LoadOrCreateDevKey(rsa, configuration.GetValue<bool>("OpenApi:Export"));
        }
        else
        {
            throw new InvalidOperationException("Jwt:PrivateKeyPem is required outside Development.");
        }

        if (rsa.KeySize < 2048)
        {
            throw new InvalidOperationException("The JWT signing key must be at least 2048 bits.");
        }

        return new AuthSigningKey(rsa, retired);
    }

    /// <summary>The public keys as a JSON Web Key Set, for services that prefer to fetch rather than configure keys.</summary>
    public object ToJwks() => new
    {
        keys = VerificationKeys.OfType<RsaSecurityKey>().Select(key =>
        {
            var parameters = key.Rsa?.ExportParameters(false) ?? key.Parameters;
            return new
            {
                kty = "RSA",
                use = "sig",
                alg = SecurityAlgorithms.RsaSha256,
                kid = key.KeyId,
                n = Base64UrlEncoder.Encode(parameters.Modulus),
                e = Base64UrlEncoder.Encode(parameters.Exponent)
            };
        })
    };

    private static void LoadOrCreateDevKey(RSA rsa, bool ephemeral)
    {
        if (ephemeral)
        {
            rsa.KeySize = 2048;
            return;
        }

        var path = KjJwtKeys.DevKeyPath;
        if (File.Exists(path))
        {
            rsa.ImportFromPem(File.ReadAllText(path));
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var generated = RSA.Create(2048);
        File.WriteAllText(path, generated.ExportPkcs8PrivateKeyPem());
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        rsa.ImportFromPem(File.ReadAllText(path));
    }
}
