# Contribution workflow

Human-facing summary also exists in root `CONTRIBUTING.md`. This file is for agents working on the fork.

## Remotes

| Remote | URL role |
| --- | --- |
| `origin` | `https://github.com/tarkovtracker-org/RatScanner` — **push here** |
| `upstream` | `https://github.com/RatScanner/RatScanner.git` — rare sync only |

Always verify `git remote -v` before push. GUI clients can default to the wrong fork.

## Supported branch workflow (authoritative for this fork)

| Branch | Role |
| --- | --- |
| `master` | **Primary integration branch** — open PRs here |
| `feat/*`, `fix/*` | Short-lived work branches created from `master` |
| Historical `develop` / other names | May exist in local clones or upstream; **not** the day-to-day integration target |

**Resolution of prior contradiction:** older `CONTRIBUTING.md` text described classical git-flow with `develop` as primary. That is **not** the supported fork workflow. Practical work is:

1. Branch from `master`.
2. Land via PR to `master` on `tarkovtracker-org/RatScanner`.
3. Do not assume direct pushes to `master` for everyday agent work; prefer PRs.

Build CI runs for PRs targeting `master` and pushes to `master`. Releases use a separate manual workflow that promotes the exact successful artifact from the selected `master` commit without rebuilding.

## Commits and PRs

- Prefer focused PRs; docs overhaul / feature / dependency upgrades should not mix without reason.
- Commit style in this repo tends toward conventional prefixes (`fix(ui):`, `feat:`, `refactor(ui):`) — match recent history on the branch.
- PRs target **`tarkovtracker-org/RatScanner`**.
- Bare `#NNN` issue refs resolve on the **fork**. Prefer full URLs for upstream history.
- Do not push unless asked (agents); do not force-push shared branches without explicit instruction.
- Explicit maintainer decisions control architecture and repository ownership. Current files or generated guidance may describe a transitional state; surface conflicts instead of silently choosing a direction.
- Never close or supersede architecture PRs, remove their remote branches, or delete source-bearing worktrees solely because another branch currently implements a different layout. Preserve the work until the maintainer-directed path is reconciled.

### Commit quality and pre-merge validation

At minimum for merge-ready PRs:

- `dotnet build RatScanner.sln`
- `dotnet test tests\RatScanner.Tests\RatScanner.Tests.csproj` (or CI equivalent) for unit behavior
- `scripts\verify.ps1 -Mode Ui` when the hosted WebView behavior or contract is affected
- `dotnet csharpier check .` when C# style is affected
- `scripts\check-analyzer-gate.ps1` when analyzer configuration changes
- `scripts\lint-markdown.ps1 -Fix` (then check) when any `*.md` changed
- `scripts\check-agent-docs.ps1` when docs/structure/packaging references change
- Manual smoke when UI or scan behavior is touched

Optional: `scripts\install-git-hooks.ps1` installs a local check that blocks commits when Markdown is staged and repository lint fails; it does not rewrite or re-stage files.

## Upstream sync

Upstream is largely inactive. If syncing:

1. Fetch `upstream`.
2. Integrate deliberately on a branch; do not casually merge ancient histories into master without review.
3. Preserve fork branding, 4.x versioning, and ProjectReference scan engine.

## Documentation with code

When a PR changes architecture, commands, packages, CI, config paths, or behavior:

1. Update root `AGENTS.md` only if a control-plane fact moved.
2. Update the matching `docs/agent-context/*.md`.
3. Update nested `AGENTS.md` if scoped rules changed.
4. Update `README.md` / `CONTRIBUTING.md` when user-facing workflow changes.
5. Never leave agent docs claiming GraphQL bulk catalog, NuGet RatEye consumption, vendored RatEye, or Linux support.

## License and attribution

- Keep `LICENSE` in published output.
- Preserve “software has been modified” notices (README, Credits).
- Scan engine source: `src/ScanEngine` submodule from `tarkovtracker-org/RatEye`.
- When both repositories change, push the RatEye commit before any RatScanner branch that references its gitlink.
