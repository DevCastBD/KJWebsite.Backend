using Microsoft.AspNetCore.DataProtection;

namespace FinanceService.Donations;

/// <summary>Field-level encryption for donor name, email and phone (ADR-014).</summary>
public interface IDonorProtector
{
    string Protect(string plaintext);

    string Unprotect(string protectedValue);
}

public sealed class DataProtectionDonorProtector(IDataProtectionProvider provider) : IDonorProtector
{
    private readonly IDataProtector _protector = provider.CreateProtector("KJWebsite.Finance.DonorFields.v1");

    public string Protect(string plaintext) => _protector.Protect(plaintext);

    public string Unprotect(string protectedValue) => _protector.Unprotect(protectedValue);
}
