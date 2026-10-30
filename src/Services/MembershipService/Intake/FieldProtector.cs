using Microsoft.AspNetCore.DataProtection;

namespace MembershipService.Intake;

/// <summary>Field-level encryption for sensitive application columns (ADR-014).</summary>
public interface IFieldProtector
{
    string Protect(string plaintext);

    string Unprotect(string protectedValue);
}

public sealed class DataProtectionFieldProtector(IDataProtectionProvider provider) : IFieldProtector
{
    private readonly IDataProtector _protector = provider.CreateProtector("KJWebsite.Membership.ApplicationFields.v1");

    public string Protect(string plaintext) => _protector.Protect(plaintext);

    public string Unprotect(string protectedValue) => _protector.Unprotect(protectedValue);
}
