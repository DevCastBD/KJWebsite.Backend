# Seed data

`projects.{en,bn}.json` and `news.{en,bn}.json` are copies of the frontend's committed snapshots
(`KJWebsite.FrontEnd/src/content/snapshots/`, taken at frontend commit `2c20a78`, 2026-10-30). They are what the public site shows today,
and `ContentSeeder` imports them into an empty database so a first deployment is not an empty site.

They are embedded in the assembly. To refresh them, copy the frontend's newer snapshots over these files; the seeder only runs against
an empty database, so existing content is never overwritten. The frontend's development-only news placeholders are deliberately not imported.
