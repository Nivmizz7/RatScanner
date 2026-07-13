# Contributing

Try to follow the [git flow](https://www.atlassian.com/git/tutorials/comparing-workflows/gitflow-workflow) development model.

The overall flow of Gitflow is:

1. A develop branch is created from master
2. A release branch is created from develop
3. Feature branches are created from develop
4. When a feature is complete it is merged into the develop branch
5. When the release branch is done it is merged into develop and master
6. If an issue in master is detected a hotfix branch is created from master
7. Once the hotfix is complete it is merged to both develop and master

## Versioning (TarkovTracker Edition)

This fork uses its **own** [semver](http://semver.org/) line so releases and bug reports are never confused with upstream RatScanner.

| | Upstream (original) | This fork |
|---|---|---|
| Product | RatScanner | RatScanner **TarkovTracker Edition** |
| Major line | `3.x` (e.g. `3.9.3`) | **`4.x`** (starts at `4.0.0`) |
| UI / logs | `v3.9.3` | `v4.0.0 · TT` / full label in logs |
| Tags | `v3.9.3` | `v4.0.0` |

Do **not** reuse or “continue” upstream patch numbers. After a breaking change to this fork, bump major; otherwise minor/patch as usual.

**Where to bump:** only `<Version>` in `src/App/RatScanner.csproj`.

```xml
<Version>4.0.0</Version>
```

**Release tags:** `vMAJOR.MINOR.PATCH` (e.g. `v4.0.1`). CI drafts a GitHub release when a `v*` tag is pushed.

| Bump | When |
|------|------|
| **Major** | Breaking change for end users of this fork |
| **Minor** | New feature / significant behavior change |
| **Patch** | Bug fix or config-only change |
| *(none)* | Documentation-only |

Version format:

```
Major.Minor.Patch
```
