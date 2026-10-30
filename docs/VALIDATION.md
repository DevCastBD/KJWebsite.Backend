# Request validation and error shape

Validation uses FluentValidation (ADR-009) through one filter in `BuildingBlocks/Validation`.

- Add a validator (`AbstractValidator<TRequest>`) in the service, register the assembly with `AddKjValidation<TMarker>()`, and attach `.WithValidation<TRequest>()` to the endpoint. No inline `if` checks.
- Query-string values are validated by the same validators; call `ValidationProblem.Create(context, failures)` from the handler.
- Use `.WithErrorCode(ValidationMessages.X)` on custom rules. Built-in rules (`NotEmpty`, `MaximumLength`, `EmailAddress`, …) are mapped automatically.

## Response

`400`, `application/problem+json`:

```json
{
  "type": "urn:kjwebsite:error:validation-error",
  "title": "Bad Request",
  "status": 400,
  "detail": "One or more fields are invalid.",
  "instance": "/api/v1/cta/submissions",
  "errors": { "formName": ["formName is required."], "translations[0].title": ["…"] },
  "error": { "code": "VALIDATION_ERROR", "message": "One or more fields are invalid." }
}
```

- `errors` keys are the camelCase JSON field paths, so the frontend can show a message next to the input.
- Messages are rendered in the caller's language (`?lang=`, then `Accept-Language`, then `en`); `bn` and `en` exist for every code (`ValidationMessages`).
- The legacy `error { code, message }` member stays beside the standard fields in v1; do not remove it before the v2 boundary (plan §4.1).

Behaviour changes: an oversized CTA `values` payload is now `400` with an `errors.values` entry (it was `413`); registration duplicate-email and weak-password failures are field errors on `email` / `password`.
