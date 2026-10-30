# BE-040 Public extras: sitemap, feeds, Bengali search, OG metadata (#47)
- Source: issue #47 (milestone 3, order BE-040)
- Type: feature
## Request
- /sitemap.xml, /feed.rss + /feed.atom per language, /api/v1/content/search with Bengali support, OG metadata per item
## Decisions
- #46 skipped (needs #34, user chose to skip); stacked on #44 branch
- Bengali search: own term index (search_terms) built with the Bengali-aware normaliser, prefix + AND matching; no PG-specific text-search config, so identical on PG and SQLite and conjuncts stay intact
- Backend sitemap is authoritative for content URLs; static routes stay the frontend's (C-14); base URL from Site:BaseUrl
- Search fails fast: 2 s budget -> 503 SEARCH_UNAVAILABLE so the frontend falls back to its index
## Out of scope
- Stemming/synonyms, typo tolerance
## Follow-ups
