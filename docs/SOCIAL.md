# Social: Open Graph images and share counts (BE-082, #86)

## Generated Open Graph images
`OgImageWorker` (hosted service, every `Social:PollSeconds`, default 30; `Social:OgEnabled`) draws a 1200x630 PNG per published item and language for news, project, event, press, success-story and page items: brand name, title and summary on a plain background. The text is shaped by QuestPDF with the embedded Noto Sans Bengali (the certificate font), so Bengali conjuncts render correctly; a test renders real Bengali and English images and checks they are valid PNGs of the right size.

- **Not on request.** `GET /api/v1/content/og-image/{type}/{slug}?lang=bn|en` only serves the stored image (ETag, `Cache-Control: public, max-age=3600`, 304 on `If-None-Match`). A missing image is 404 and the page falls back to the default image. A crawler can never make the server render. `POST /api/v1/admin/content/social/og/generate` (`content.publish`) runs one pass immediately (up to 50) to skip the wait after a publish.
- **Redrawn only when needed**: each image keeps a hash of the title, summary, language, brand and template version; only a change redraws that one language.
- **The item's featured image is not embedded**: fetching an editor-supplied URL from the server is an SSRF risk, and without a media pipeline there is nothing safe to read. Branding and text only for now. The existing `GET /api/v1/content/og/{type}/{slug}` still returns the metadata (title, description, image) for the OG tags; point `image` at the generated URL for items with no image of their own.
- Brand names: `Social:BrandNameEn`, `Social:BrandNameBn`.
- **Not verified here**: how Facebook, X, LinkedIn and WhatsApp render the previews. That needs the public site URL and each platform's debugger; do it once the frontend serves the tags.

## Share counts
`POST /api/v1/content/shares/{type}/{slug} {channel}` (anonymous; facebook, x, linkedin, whatsapp, copy, other) counts a share of a published item; `GET` returns counts by channel; `GET /api/v1/admin/content/social/top-shared` (`content.view`) ranks items. A bare counter, no person, address or device stored. Counts are indicative: anyone can call the endpoint.

## Cross-posting: not built
Scheduled cross-posting to social platforms needs per-platform credentials and breaks whenever a platform changes its API; the issue itself asks whether it earns its maintenance. Left out on purpose; revisit if the organisation names a platform it will really use.
