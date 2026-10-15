# ADR-009: FluentValidation with a shared endpoint filter

- Status: Accepted
- Date: 2026-10-14
- Ratified by: Md Tahmidul Abedin (@tahmidsparrow)
- Source: [issue #12](https://github.com/DevCastBD/KJWebsite.Backend/issues/12)

## Context

Request validation today is ad hoc inline checks in each Minimal API handler (e.g. the `string.IsNullOrWhiteSpace` checks in `AuthIdentityService/Program.cs`), repeated per endpoint with no shared error shape. As more endpoints and services are added, this does not scale and produces inconsistent error responses.

## Decision

Adopt FluentValidation for request validation, invoked through a shared Minimal API endpoint filter in `BuildingBlocks` that runs the matching validator before the handler executes and returns the existing `ApiError`-shaped response on failure.

## Rejected alternatives

- `DataAnnotations`: less expressive for conditional and cross-field rules, and harder to unit test in isolation from the request pipeline.
- Continuing ad hoc inline checks: already produces inconsistent validation error shapes between endpoints and does not compose as rule complexity grows.

## Consequences

- New endpoints register a FluentValidation validator and get consistent `ApiError` validation responses through the shared filter for free.
- Existing ad hoc inline checks in AuthIdentityService and CtaSubmissionService migrate to FluentValidation incrementally, not all at once.
