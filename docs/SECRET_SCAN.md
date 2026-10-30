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

## 2026-10-31 re-run (issue #36)

Command: `tools/scan-secrets.sh --patterns` (every added line of `git log --all -p`, excluding the read-only `legacy/` snapshots, plus all tracked files). `gitleaks` was not installed on the machine that ran it, so this is still a pattern scan; the script uses gitleaks automatically when it is present, and `.gitleaks.toml` carries the same allowlist.

Result: no private keys, cloud or provider tokens, JWTs or credential assignments beyond the reviewed placeholders in the table above. Nothing real was ever committed, so no remediation and no history rewrite. The shared dev password and token placeholders are public development values; the Development seed account is disabled everywhere else, and `dev-admin-token` authenticates nothing since #24.

Before the first public deployment: install gitleaks (`brew install gitleaks`) and run `tools/scan-secrets.sh` once more. Rotation and the rest of the secrets policy are in [SECRETS.md](SECRETS.md).
