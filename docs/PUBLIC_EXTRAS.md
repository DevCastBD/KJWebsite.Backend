# Public extras: search, sitemap, feeds, Open Graph (BE-040, #47)

## Search — `GET /api/v1/content/search?q=&lang=&type=&limit=&offset=`
PostgreSQL's built-in text-search configurations cannot tokenise Bengali, so search uses its own term index (`search_terms`), identical on PostgreSQL and SQLite.
- **Tokenising** (`SearchTokenizer`): normalise like tag names (NFC so precomposed and decomposed য়/ড়/ঢ় match, zero-width joiners removed, Bengali digits to ASCII, lower case), then split on anything that is not a letter, digit or combining mark. Vowel signs and the virama are combining marks, so **conjuncts such as ক্ষ, ্র, বিশ্ব stay inside one term**, and Bengali and English words in one text or query are both kept.
- **Matching:** every query term (max 8) must be a *prefix* of a term in the requested language (AND), so "ক্ষমতা" finds "ক্ষমতায়ন". Ranked by field weight (title 3 > summary 2 > body 1, summed over query terms), then newest first. Only published/active, live items with a translation in `lang` are returned (no fallback in search).
- **Index upkeep:** rebuilt for an item after every content change (the same hook that writes revisions: edits, workflow moves, scheduled publishes, restores); a startup pass indexes anything not indexed yet (seeded content, upgrades).
- **Fails fast:** a 2 s budget (`Search:TimeoutMs`) then `503 SEARCH_UNAVAILABLE`, so the frontend falls back to its client-side index instead of hanging.
- Not done: stemming, synonyms, typo tolerance; very large corpora would want a PostgreSQL trigram/GIN index.

## Sitemap — `GET /sitemap.xml`
One `<url>` per published item **per language**, each with `xhtml:link hreflang` alternates and `lastmod`. Drafts, in-review, scheduled, archived and deleted items are never listed. Types from `Site:SitemapTypes` (default `project,news,page,press`). **Authoritative for content URLs only**; the frontend keeps generating the static routes (C-14), so serve the two as a sitemap index rather than letting both claim the same URLs.

## Feeds — `GET /feed.rss?lang=` and `/feed.atom?lang=`
RSS 2.0 and Atom, newest 30 published items of `Site:FeedTypes` (default `news`), **one language each**: an item without a translation in that language is left out (no fallback). Characters XML forbids are stripped so a feed never goes invalid.

## Open Graph — `GET /api/v1/content/og/{type}/{slug}?lang=`
`title`, `description` (summary, else the start of the body), canonical `url`, absolute `image`, `type` (`article` for news/press, else `website`), `locale` (`bn_BD`/`en_US`), `alternateLocales`, `alternates` (per-language URLs), `siteName`, `publishedTime`, `modifiedTime`, `tags`. 404 for anything unpublished.

## Configuration
`Site:BaseUrl` (required for the sitemap, feeds and OG; otherwise `503 SITE_URL_NOT_CONFIGURED`; Development defaults to `http://localhost:5173`), `Site:Name`, `Site:ItemUrlTemplate` (default `{base}/{lang}/{segment}/{slug}`, matching the frontend's language-prefixed routes), `Site:SitemapTypes`, `Site:FeedTypes`, `Search:TimeoutMs`. The gateway forwards `/sitemap.xml`, `/feed.rss` and `/feed.atom` to the content service.
