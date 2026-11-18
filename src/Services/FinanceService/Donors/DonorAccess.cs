using FinanceService.Data;
using FinanceService.Data.Entities;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace FinanceService.Donors;

internal static class DonorAccess
{
    internal static async Task<DonorEntity?> FindAsync(HttpContext context, FinanceDbContext db)
    {
        var subject = context.User.FindFirst(KjClaims.Subject)?.Value;
        return string.IsNullOrEmpty(subject) ? null : await db.Donors.AsNoTracking()
            .FirstOrDefaultAsync(d => d.AccountSubject == subject, context.RequestAborted);
    }

    internal static string? RefusalCode(DonorEntity donor) => donor.Status == DonorStatuses.Inactive
        ? "DONOR_INACTIVE"
        : donor.AccountStatus == AccountStatuses.Disabled ? "ACCOUNT_DISABLED" : null;

    internal static async Task<DonorEntity?> EligibleAsync(HttpContext context, FinanceDbContext db)
    {
        var authorization = context.RequestServices.GetRequiredService<IAuthorizationService>();
        if (!(await authorization.AuthorizeAsync(context.User, null, KjPolicies.DonorSelf)).Succeeded)
        {
            return null;
        }

        var donor = await FindAsync(context, db);
        return donor is not null && RefusalCode(donor) is null ? donor : null;
    }
}
