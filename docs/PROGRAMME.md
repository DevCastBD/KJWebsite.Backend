# Programme delivery (BE-073, #77)

In the Membership service (`src/Services/MembershipService/Programme/`), under `/api/v1/admin/membership/programme`. It models the Pathshala delivery pattern directly: institutions take part in a project, sessions are logged against a project and an institution, and the numbers that leave the module are aggregates with a trail back to the logged rows.

## Who can see what
Institution data is sensitive: MoUs name signatories, and student counts plus addresses identify where children are.
- `programme.manage` (Admin, EventManager): institutions (address encrypted), enrolments, MoUs and their documents, outcome metrics, session lists, project summaries and published figures.
- `programme.log` (EventManager, plus every holder of `programme.manage` in the Admin roles): log sessions only. A field logger sees no address, MoU, signatory or count.

## Model
- **Institution** (`kind`: school, madrasa, mission, other; bilingual name; encrypted `address`; coarse `district`; archived flag).
- **Enrolment**: `PUT .../beneficiaries/{id}/enrolments/{projectSlug}` with `students`; this is the per-institution student count for that project.
- **MoU**: `signedOn`, `validUntil`, encrypted signatory and role. The signed document is `PUT .../mous/{id}/document` (PDF, JPEG or PNG up to 10 MB, content checked against its declared type, stored in the attachment store) and `GET` of the same URL; both need `programme.manage` and the download is `no-store`. MoU documents are never served from a public or guessable path.
- **Session log**: `POST .../sessions` and `POST .../sessions/batch` (up to 200). Each item carries the field app's own `clientId`; sending it again returns `duplicate` and changes nothing, one bad item does not stop the batch, and a session may be up to a year old (never in the future), so delayed and batched field submission works.
- **Outcome metric**: `PUT .../outcomes` upserts (project, `metricKey`, period) with a value and unit. Which keys exist is defined once in the impact metrics work (BE-085, #89), not here.

## Project summary and published figures
- `GET .../projects/{slug}/summary`: the project's institutions, MoUs, `sessionsDelivered`, `studentsReached` (sum of its enrolments), attendance total and outcomes.
- `GET .../figures`: **aggregates only** (institutions, students reached, sessions delivered, in total and per project) with a `fingerprint`, a hash of the logged session ids and enrolment ids and counts behind it. No names, addresses, MoUs or districts. Impact metrics read this endpoint, so anything published traces to logged sessions and never exposes an institution.
