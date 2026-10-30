namespace MembershipService.Self;

/// <summary>The member's own profile in the shape the portal reads. `pending` holds changes waiting for, or refused by, a reviewer.</summary>
public sealed record PortalProfile(
    string MemberId,
    string MemberSince,
    string Email,
    string FirstName,
    string LastName,
    string NameBn,
    string DateOfBirth,
    string BloodGroup,
    string Phone,
    string Address,
    string Expertise,
    IReadOnlyDictionary<string, PendingChange> Pending,
    DeletionRequestView? DeletionRequest,
    bool HasPhoto);

public sealed record PendingChange(string Value, string Status, string? Reason, DateTimeOffset SubmittedAt);

public sealed record ProfilePatch(Dictionary<string, string?>? Changes);

public sealed record Consents(bool Name, bool Photograph, bool Expertise);

public sealed record NotificationPrefs(bool News, bool Events, bool Membership);

public sealed record DeletionRequestView(string Reference, DateTimeOffset RequestedAt);

public sealed record ProfileChangeView(string Id, string MemberId, string MemberCode, string MemberName, string Field, string CurrentValue, string NewValue, string Status, string? Reason, DateTimeOffset SubmittedAt, DateTimeOffset? DecidedAt);

public sealed record DecisionRequest(string? Reason);

public sealed record CardView(string MemberCode, CardName Name, string MemberSince, string QrPayload, string QrSvg);

public sealed record CardName(string En, string Bn);
