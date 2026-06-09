# Full Codebase Optimization — Design Spec

**Date:** 2026-06-08
**Scope:** All ~55 source `.cs` files in `ShokoRelay/`
**Goal:** Both code simplification (no behavior change) and runtime performance improvements, applied via 5 parallel agents each owning one subsystem.

---

## Approach

Parallel agent sweep (Option A). Five agents run concurrently, each scoped to one subsystem. After all agents complete, a build verification pass (`dotnet build`) confirms no compilation errors. Any uncertain changes flagged by agents receive a manual review.

---

## Subsystem Assignments

| Agent | Files |
|---|---|
| **Helpers** | `TagHelper`, `TextHelper`, `ContentRatingHelper`, `CastHelper`, `ImageHelper`, `OverrideHelper`, `ShokoExtensionHelper`, `MapHelper`, `LogHelper`, `TaskHelper` |
| **Plex** | `PlexApi`, `PlexAuth`, `PlexClient`, `PlexCollections`, `PlexHelper`, `PlexMapping`, `PlexMetadata`, `PlexConstants` |
| **VFS** | `VfsBuilder`, `VfsAssetLinker`, `VfsWatcher`, `VfsShared`, `VfsHelper` |
| **Sync** | `SyncToPlex`, `SyncToShoko`, `SyncHelper` |
| **Services & Controllers** | `CollectionService`, `CriticRatingService`, `ImageSyncService`, `FfmpegService`, `ShokoImportService`, `SourceLinkService`, `AnimeThemesMp3Generator`, `AnimeThemesApi`, `AnimeThemesHelper`, `AnimeThemesMapping`, `AnimeThemesWebm`, all Controllers, `ShokoRelay.cs`, `ShokoRelayConstants.cs`, `ConfigProvider`, `RelayConfig`, `PlexConfig` |

---

## What Each Agent Applies

### Simplification (no behavior change)
- Fix LINQ chain ordering (`Where` before `Select`)
- Remove redundant `.ToList()` calls where enumeration is sufficient
- Collapse multi-step LINQ chains into single expressions where readable
- Replace verbose null-checks with null-conditional `?.` operators
- Hoist repeated property access into local variables
- Replace anonymous objects with tuples or records where cleaner
- Remove misleading or redundant doc comments

### Performance
- Remove `.ToList()` before `.Any()` / `.Count()` — use `.Any()` directly on `IEnumerable`
- Replace `List<T>.Contains` inside loops with `HashSet<T>` lookups
- Add missing `StringComparison.OrdinalIgnoreCase` on hot-path string comparisons
- Hoist repeated `Settings` / config property reads out of loops
- Collapse `async` methods that `await` a single expression into a direct `return task` (removing unnecessary state machine allocation)
- Prefer `string.Concat` or `StringBuilder` over interpolation in tight loops

---

## Guardrails

- No new features or behavior changes
- No refactoring across file boundaries
- No changes to public API signatures (method names, parameter types, return types)
- Flag but do not change anything uncertain — leave a comment for manual review

---

## Verification

1. Each agent reports a summary of changes made and anything flagged as uncertain
2. After all agents complete: `dotnet build` in the repo root — must succeed with zero errors
3. Any uncertain flags from agents get a manual review pass before committing

---

## Out of Scope

- Adding unit tests
- Architectural changes (moving classes, splitting files)
- Dependency upgrades
- Dashboard / frontend files
