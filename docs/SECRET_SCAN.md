# Git history secret scan

Run for issue #24 / #17 (plan §7: history must contain no secrets). Scanned every added line in `git log --all -p` (2026-10-30) for private keys, cloud and GitHub/Slack/Stripe/Google tokens, JWT-shaped strings, and `password`/`secret`/`api key` assignments. No dedicated scanner was installed, so this was a pattern scan, not a gitleaks run.

## Findings

| Finding | Where | Verdict |
|---|---|---|
| `dev-admin-token` | `ContentService` (removed in #24), early tests and docs | Development placeholder, never deployed. It no longer authenticates anything, so it cannot be used. Left in history. |
| `admin@site.org` / `admin123` | seed code, tests, README | Public development account. Exists only in Development; disabled by the seeder anywhere else. |
| `Password=postgres` | Compose files (removed) and doc examples | Local example credentials, not a real server. |
| `password123`, `correct-horse-battery` | tests | Test values. |

No private keys, cloud credentials, API tokens or real connection strings were found.

`legacy/**/*.sql` contains hashed passwords from a 2019 test database (see `legacy/README.md`). They are test data from a system that is not deployed.

## Remediation

None needed beyond what shipped: the shared secret is gone from the code, and nothing real was ever committed, so no history rewrite is proposed (a rewrite would also invalidate every clone and open PR). Re-run a proper scanner (gitleaks) before the first public deployment; that is tracked with secrets management in #36.
