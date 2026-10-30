namespace MembershipService.Lifecycle;

public sealed record StatusChangeRequest(string? To, DateOnly? EffectiveOn, string? Note, string? Reason);

/// <summary>`Reason` is the termination reason: null with `ReasonRestricted` true for callers without `membership.terminate`.</summary>
public sealed record LifecycleEvent(string? From, string To, DateOnly EffectiveOn, DateTimeOffset At, string By, string? Note, string? Reason, bool ReasonRestricted);

public sealed record LifecycleView(string MemberId, string MemberCode, string Status, IReadOnlyList<string> NextStatuses, IReadOnlyList<LifecycleEvent> History, Tenure Tenure);
