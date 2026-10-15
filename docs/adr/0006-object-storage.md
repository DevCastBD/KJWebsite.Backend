# ADR-006: Cloudflare R2 for object storage

- Status: Accepted
- Date: 2026-10-14
- Ratified by: Md Tahmidul Abedin (@tahmidsparrow)
- Source: [issue #12](https://github.com/DevCastBD/KJWebsite.Backend/issues/12)

## Context

No file or blob storage exists yet. Upcoming work (member documents, event photos, donation receipts) needs a place to put binary content that is not the relational database.

## Decision

Use Cloudflare R2 (S3-compatible API) for all object storage. No blobs are stored in PostgreSQL.

## Rejected alternatives

- Storing blobs in PostgreSQL: bloats database size and backups, and degrades query performance on otherwise small relational tables.
- AWS S3: equally capable and S3-compatible, but R2 has no egress fees, which matters more for a non-profit budget than any AWS-specific feature the project needs.

## Consequences

- Services that handle uploads depend on a shared storage client in `BuildingBlocks`, talking to R2 over its S3-compatible API.
- Access keys are secrets and must be provisioned per environment, not committed.
- Uploads and downloads go through signed URLs rather than proxying file bytes through a service.
