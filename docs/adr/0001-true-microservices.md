# ADR-001: Retain independently deployable microservices

- Status: Accepted
- Date: 2026-10-12
- Ratified by: Md Tahmidul Abedin (@tahmidsparrow)
- Source: [issue #105](https://github.com/DevCastBD/KJWebsite.Backend/issues/105)

## Context

The backend currently has a YARP gateway and separate Identity, Content, and CTA services. The proposed modular monolith would reduce deployment and operational work for a volunteer team. It would also join the services into one application before P1 adds more domains.

The organisation may need to deploy applications separately under different domains, or host them together later. Independent service boundaries preserve both hosting choices. Domain routing alone does not require microservices; independent deployment is the deciding capability.

## Decision

Keep true microservices behind the gateway. Each domain owns its service, data model, migrations, and deployment lifecycle. New domains become separate services when their boundaries are defined. Services may share infrastructure, including a PostgreSQL instance with a separate schema per service, but must not write directly to another service's data.

Use gateway routing and configuration to serve services under one public domain or distinct domains as needed. Preserve the API contract and frontend feature flags while hosts change. Service-to-service calls must authenticate; a service registry for discovery and distributed tracing are P1 requirements. Share cross-cutting contracts and utilities through versioned BuildingBlocks packages, and use APIs or events for cross-service coordination.

## Rejected alternative

Consolidate the services into a modular monolith behind YARP. This would reduce the number of deployables, network hops, migrations, and operational failure modes. It is rejected because the ratifier prioritised the flexibility to separate application hosting and domains without first extracting modules from a combined application.

## Consequences

- Service teams can deploy independently and choose shared or separate hosts and domains.
- P1 needs service-to-service authentication, discovery, tracing, per-service migration ownership, and failure handling from the start.
- Cross-service workflows need explicit API or event contracts; database access across service boundaries is prohibited.
- The plan estimates roughly 30% more effort per phase than the proposed modular monolith. Hosting, observability, and on-call costs must be tracked as the service count grows.
- The gateway remains the public entry point unless a later decision explicitly changes the exposure and security model.
