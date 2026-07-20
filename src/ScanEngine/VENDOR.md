# Vendored scan engine (historical RatEye)

Folder: `src/ScanEngine/`
Assembly / namespaces: still **`RatEye`** (unchanged for now to avoid a large API rename).

Sources vendored from <https://github.com/RatScanner/RatEye> at tag `v4.0.1`
(`862b78002a59286aaed6d4d94dce87ce9a989462`).

This project is referenced by `src/App` via `ProjectReference` so image-processing
changes land in the same PR as the app. Do not re-add the RatEye NuGet package.

Test assets from upstream `RatEyeTest` are intentionally not included (large binaries).

## License note

The vendored `v4.0.1` tag did not contain a `LICENSE` file. The original author subsequently
published the RatEye terms in commit [`98f562f`](https://github.com/RatScanner/RatEye/commit/98f562f38b4a9c9d330ef700971d6adf5183595d)
("Add License"). Those terms permit copying, distribution, and derivative works subject to
their limitations and notice requirements. They are byte-for-byte identical to this
repository's root [`LICENSE`](../../LICENSE), which is included in published packages.

This fork has modified the vendored sources after `v4.0.1`; this file and the repository
attribution are the prominent modification and provenance notices. Preserve them when
redistributing the scan engine.
