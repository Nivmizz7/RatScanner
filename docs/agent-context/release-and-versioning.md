# Release and versioning

## Product versioning

| | Historical upstream | This project |
| --- | --- | --- |
| Line | 3.x | **4.x** starting at 4.0.0 |
| Tag form | `v3.x.x` | `v4.x.x` (pre-release suffixes supported, e.g. `v4.0.0-beta.1`) |
| UI token | — | `v4.x.x` (`MenuVM.VersionDisplay`) |
| Log label | — | `RatConfig.FullVersionLabel` / `Constants.Branding` |

**Bump only** `<Version>` in `src/App/RatScanner.csproj`. Do not mirror historical upstream patch numbers.

ScanEngine's own `<Version>` is historical engine packaging metadata; product releases are App-driven.

### Semver guidance

| Bump | When |
| --- | --- |
| Major | Breaking change for end users |
| Minor | Feature / significant behavior change |
| Patch | Bug fix or config-only user impact |
| Pre-release suffix | Beta/RC phases (e.g. `-beta.1`, `-rc.1`) |
| None | Documentation-only |

### Prevention of accidental upstream reuse

- Do not tag or ship `3.x` on this project.
- Issues that cite only `3.9.x` are almost certainly not from the 4.x line.

## Local publish

```bat
publish.bat
```

Matches the CI packaging intent:

- `dotnet publish src/App/RatScanner.csproj -c Release -o publish --runtime win-x64 -p:PublishSingleFile=true --self-contained true`
- Ensure `LICENSE` in output
- Download and extract latest RatScannerData into `publish\Data`
- Zip to `RatScanner.zip`

Day-to-day coding should use `dev.bat`, not publish.

## CI release path

Workflows:

- `.github/workflows/build.yml` — PRs to `master` and pushes to `master`.
- `.github/workflows/release.yml` — manual artifact promotion from `master` only.

The build job (Windows, .NET 10.x, `contents: read`) runs agent-docs regression/integrity checks → Markdown and C# formatting → restore/build/test Release → .NET/npm vulnerability audits → publish single-file → validate LICENSE + Data → upload `RatScanner.zip` as an immutable workflow artifact.

The release workflow does **not** rebuild. It resolves `<Version>` from the selected `master` commit, requires a successful push-triggered Build run for that exact commit, and downloads that run's `RatScanner.zip` with GitHub's artifact digest verification enabled. It then validates the zip, refuses to overwrite an existing tag or draft/published release, auto-creates the tag, and publishes the exact CI-tested package **directly as Latest** (`draft: false`, `prerelease: false`, `make_latest: true`). Publishing as a non-prerelease Latest release is mandatory: the in-app updater reads `/releases/latest`, which skips drafts and pre-releases. Only the Release workflow receives `actions: read` and `contents: write`; PR and branch-push builds remain read-only.

### How to cut a release

Preferred (one click, no local git or duplicate build):

1. Bump `<Version>` in `src/App/RatScanner.csproj` (e.g. `4.0.1-beta`) and land it on `master`.
2. Wait for the push-triggered **Build** workflow on that `master` commit to pass.
3. GitHub → **Actions** → **Release** → **Run workflow** from `master`.
4. CI promotes the successful Build artifact, tags `v<Version>`, and publishes it as the Latest release. Running installs pick it up on next launch.

`GitHubUpdateService` compares the numeric version part, so bump the numeric version every release (`4.0.0-beta` → `4.0.1-beta`); never ship two releases sharing the same numeric base (e.g. `4.0.0-beta` then `4.0.0-beta.2`), or the second will not be offered as an update.

`softprops/action-gh-release` must stay pinned to a commit SHA on the TarkovTracker org **selected actions** allowlist. GitHub rejects the entire workflow at startup (including the non-release build job) if the SHA is not listed. Process for bumps: (1) review the new softprops release, (2) add the SHA via org Actions selected-actions policy (`admin:org`), (3) update this workflow pin, (4) only then remove older softprops SHAs from the allowlist once no org workflow still references them.

## Update channel in-app

`GitHubUpdateService` checks `tarkovtracker-org/RatScanner` releases and applies zip swap. It does **not** use upstream `api.ratscanner.com` updater endpoints. It reads `/releases/latest`, so a release is only offered when published as a non-prerelease Latest release, and only when its numeric version is higher (or it is the matching stable of an installed pre-release).

## Checklist before tagging

1. Version bumped in App csproj; the release tag must match it exactly with a leading `v`.
2. `dotnet test` / build green.
3. `scripts\check-agent-docs.ps1` green if docs/packaging references changed.
4. Publish smoke or trust CI.
5. Changelog/notes ready for draft release body if needed.
6. Deploy: **Actions → Release → Run workflow** from `master`.
7. Confirm the auto-published release is marked **Latest** (not pre-release) and has `RatScanner.zip` attached.

## Branding

Product name is `RatScanner` (`Constants.Branding.Name`). User-agent strings use `RatScanner/<version>`. Keep modification notices (README attribution, Credits/About) intact for license compliance.
