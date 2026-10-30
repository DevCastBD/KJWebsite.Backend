#!/usr/bin/env bash
# Secret scan for the working tree and the whole git history (issue #36; plan v2 has no CI, so run this before each release).
#   tools/scan-secrets.sh            # gitleaks if installed, otherwise the built-in pattern scan
#   tools/scan-secrets.sh --patterns # force the built-in pattern scan
# Exit status is non-zero when something that is not on the allowlist is found.
set -euo pipefail
cd "$(dirname "$0")/.."

if [[ "${1:-}" != "--patterns" ]] && command -v gitleaks >/dev/null 2>&1; then
  echo "gitleaks: scanning git history"
  gitleaks detect --source . --config .gitleaks.toml --redact --no-banner
  echo "gitleaks: scanning working tree"
  gitleaks detect --source . --config .gitleaks.toml --redact --no-banner --no-git
  echo "No secrets found."
  exit 0
fi

echo "gitleaks not used; running the built-in pattern scan (install gitleaks for a stronger check)."

# Added lines only: a removed secret still lives in history, so history is scanned as patches.
patterns=(
  '-----BEGIN [A-Z ]*PRIVATE KEY-----'
  'AKIA[0-9A-Z]{16}'
  'gh[pousr]_[A-Za-z0-9]{36,}'
  'xox[baprs]-[A-Za-z0-9-]{10,}'
  'sk_(live|test)_[A-Za-z0-9]{16,}'
  'AIza[0-9A-Za-z_-]{35}'
  'eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}'
  '(password|passwd|pwd|secret|api[_-]?key|token)[A-Za-z_]*["'"'"']?[ ]*[:=][ ]*["'"'"'][^"'"'"' $<{}()]{12,}["'"'"']'
  'Password=[^;" ]{3,}'
)
regex=$(IFS='|'; echo "${patterns[*]}")

# Known, reviewed placeholders (docs/SECRET_SCAN.md). Keep this list short; every entry needs a reason there.
allow='dev-admin-token|admin123|admin@site\.org|Password=postgres|password123|correct-horse-battery|Str0ng!|hunter2|test-only|example\.(com|org|test)|PrivateKeyPem|PublicKeyPem|TestJwtKeys|Jwt__|weak_password|ServerToken|eyJhbGciOiJSUzI1NiJ9\.eyJzdWIiOiJ1c3JfMSJ9|SecretKey|turnstileToken|X-Postmark-Server-Token|RefreshToken|refresh_token|access_token|AccessToken|NewPassword|newPassword|ResetPassword|reset-password|forgot-password|ConfirmEmail|confirm-email|token\b.*(string|Token)'

found=0
report() { # label, hits
  if [[ -n "$2" ]]; then echo "== $1"; echo "$2" | head -40; found=1; fi
}

# History: every line ever added on any branch.
report "git history (added lines)" "$(git log --all -p --no-color -- . ':!legacy' 2>/dev/null | grep -E '^\+[^+]' | grep -Eiv -e "$allow" | grep -Ei -e "$regex" | cut -c1-160 || true)"
# Working tree: tracked files only (git ls-files skips bin/obj and other ignored output).
report "working tree" "$(git ls-files -z -- . ':!legacy' | xargs -0 grep -InE -e "$regex" 2>/dev/null | grep -Eiv -e "$allow" | cut -c1-160 || true)"

if [[ $found -ne 0 ]]; then
  echo "Possible secrets found. Rotate anything real (docs/SECRETS.md), then remove or allowlist it with a reason."
  exit 1
fi
echo "No secrets found."
