using MembershipService.Data;
using Microsoft.EntityFrameworkCore;

namespace MembershipService.Review;

/// <summary>Member codes are KJ-yyyy-nnnn, numbered from 0001 each year. The unique index settles two approvals racing for one number.</summary>
public static class MemberCodes
{
    public static string Format(int year, int number) => $"KJ-{year}-{number:D4}";

    public static async Task<string> NextAsync(MembershipDbContext db, int year, int skip, CancellationToken cancellationToken)
    {
        var prefix = $"KJ-{year}-";
        var codes = await db.Members.AsNoTracking().Where(m => m.MemberCode.StartsWith(prefix)).Select(m => m.MemberCode).ToListAsync(cancellationToken);
        var highest = codes.Select(c => int.TryParse(c[prefix.Length..], out var n) ? n : 0).DefaultIfEmpty(0).Max();
        return Format(year, highest + 1 + skip);
    }
}
