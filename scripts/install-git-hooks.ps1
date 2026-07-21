<#
.SYNOPSIS
  Install an optional local pre-commit Markdown check.

.DESCRIPTION
  Writes a pre-commit hook that runs markdownlint-cli2 when Markdown is staged.
  The hook never rewrites or re-stages working-tree content. Non-Markdown commits
  are unaffected. The hook is local-only (not committed under .git/hooks).

  Re-run after clone if you want the hook. CI still enforces check mode.
#>
[CmdletBinding()]
param(
    [string]$RepoRoot = ''
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
    $RepoRoot = (Resolve-Path (Join-Path $scriptDir '..')).Path
}

$hooksDir = Join-Path $RepoRoot '.git\hooks'
if (-not (Test-Path -LiteralPath $hooksDir)) {
    Write-Host 'FAIL: .git/hooks not found. Run from a git clone.' -ForegroundColor Red
    exit 1
}

$hookPath = Join-Path $hooksDir 'pre-commit'
$hookBody = @'
#!/bin/sh
# Auto-installed by scripts/install-git-hooks.ps1
# Check Markdown with markdownlint-cli2 when at least one Markdown file is staged.

REPO_ROOT=$(git rev-parse --show-toplevel 2>/dev/null) || exit 0
cd "$REPO_ROOT" || exit 0

if ! git diff --cached --name-only --diff-filter=ACMR -- '*.md' | grep -q .; then
  exit 0
fi

if ! command -v npm >/dev/null 2>&1; then
  echo "pre-commit: npm is required for Markdown lint. Install Node.js or remove this optional hook." >&2
  exit 1
fi

if [ ! -f node_modules/markdownlint-cli2/markdownlint-cli2-bin.mjs ]; then
  if [ -f package-lock.json ]; then
    npm ci --no-fund --no-audit || exit $?
  else
    npm install --no-fund --no-audit || exit $?
  fi
fi

if [ ! -f node_modules/markdownlint-cli2/markdownlint-cli2-bin.mjs ]; then
  echo "pre-commit: markdownlint-cli2 is unavailable after npm install." >&2
  exit 1
fi

npm run lint:md
STATUS=$?
if [ $STATUS -ne 0 ]; then
  echo "pre-commit: Markdown lint failed. Run scripts/lint-markdown.ps1 -Fix, review, and stage the result." >&2
  exit $STATUS
fi

exit 0
'@

# Git for Windows accepts sh hooks. Write without BOM for shell compatibility.
$utf8NoBom = New-Object System.Text.UTF8Encoding $false
[System.IO.File]::WriteAllText($hookPath, $hookBody, $utf8NoBom)

Write-Host "Installed pre-commit hook: $hookPath" -ForegroundColor Green
Write-Host 'Commits with staged *.md files will be blocked when repository Markdown lint fails.' -ForegroundColor Cyan
Write-Host 'Uninstall: delete .git/hooks/pre-commit' -ForegroundColor DarkGray
exit 0
