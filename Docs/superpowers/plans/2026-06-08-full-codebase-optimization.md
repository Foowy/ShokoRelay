# Full Codebase Optimization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Apply code simplification and runtime performance improvements across all ~55 `.cs` source files in `ShokoRelay/`, split into 5 parallel subsystem agents, followed by a build verification pass.

**Architecture:** Five parallel agents each own one subsystem. Each agent reads its files, applies two categories of changes (simplification + performance), and reports anything uncertain. After all agents complete, a `dotnet build` confirms zero compilation errors.

**Tech Stack:** C# / .NET 10, ASP.NET Core, NLog, System.Text.Json, Newtonsoft.Json (CastHelper only)

---

## Shared Rules (apply to ALL tasks)

Every agent follows these guardrails without exception:

- **No behavior changes.** Every change must be semantically equivalent to the original.
- **No cross-file refactoring.** Do not move, rename, or extract across file boundaries.
- **No public API changes.** Method names, parameter types, and return types are frozen.
- **Flag uncertain items.** If a change is ambiguous, leave an inline `// REVIEW:` comment and skip it.
- **No new comments.** Only add `// REVIEW:` markers where flagged.

---

## Simplification patterns to apply (all agents)

For each file, look for and fix:

1. **LINQ ordering** — `Where` must come before `Select` (avoids projecting items that get filtered):
   ```csharp
   // Before
   items.Select(x => x.Foo).Where(f => f != null)
   // After
   items.Where(x => x.Foo != null).Select(x => x.Foo)
   ```

2. **Redundant `.ToList()` before read-only consumption** — remove when the result is only enumerated (`.Any()`, `foreach`, passed to a method taking `IEnumerable`):
   ```csharp
   // Before
   var list = items.Where(x => x.Active).ToList();
   if (list.Any()) ...
   // After
   var active = items.Where(x => x.Active);
   if (active.Any()) ...
   ```
   **Exception:** Keep `.ToList()` when the result is indexed, counted via `.Count` (property not method), mutated, or enumerated more than once.

3. **Verbose null-checks replaceable with null-conditional**:
   ```csharp
   // Before
   string? name = creator != null ? creator.Name : null;
   // After
   string? name = creator?.Name;
   ```

4. **Repeated property access inside a method** — hoist to a local when accessed 3+ times:
   ```csharp
   // Before
   if (Settings.Advanced.Parallelism > 0) { ... Settings.Advanced.Parallelism ... }
   // After
   int parallelism = Settings.Advanced.Parallelism;
   if (parallelism > 0) { ... parallelism ... }
   ```

5. **Unnecessary `async`/`await` wrapping a single awaitable** — collapse to direct `return`:
   ```csharp
   // Before
   public async Task<Foo> GetFooAsync() => await _service.GetAsync();
   // After
   public Task<Foo> GetFooAsync() => _service.GetAsync();
   ```
   **Only apply when there is no `try/catch`, `using`, or logic after the await.**

6. **Anonymous objects where a tuple is cleaner** — only when the anonymous object is local (not returned or serialized):
   ```csharp
   // Before
   var x = new { Name = n, Id = id };
   // After
   var x = (Name: n, Id: id);
   ```
   **Do NOT change anonymous objects that are returned as `object` to Plex JSON serialization** — these must stay as anonymous objects.

---

## Performance patterns to apply (all agents)

1. **`.ToList()` before `.Any()` or `.Count()`** — remove the `.ToList()`:
   ```csharp
   // Before
   items.ToList().Any(x => x.IsActive)
   // After
   items.Any(x => x.IsActive)
   ```

2. **`List<T>.Contains` in a loop** — replace with `HashSet<T>`:
   ```csharp
   // Before
   var names = new List<string> { "a", "b" };
   if (names.Contains(input)) ...
   // After
   var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "a", "b" };
   if (names.Contains(input)) ...
   ```

3. **Missing `StringComparison` on hot-path `.Equals` / `.Contains` / `.StartsWith`**:
   ```csharp
   // Before
   name.Equals("Season")
   // After
   name.Equals("Season", StringComparison.OrdinalIgnoreCase)
   ```
   Use `Ordinal` for fixed-ASCII tokens, `OrdinalIgnoreCase` for user-visible names. Do NOT change calls that already have a `StringComparison` argument.

4. **Repeated `Settings.*` reads inside a loop** — hoist above the loop:
   ```csharp
   // Before
   foreach (var item in items) {
       if (Settings.Advanced.EnableX) { ... }
   }
   // After
   bool enableX = Settings.Advanced.EnableX;
   foreach (var item in items) {
       if (enableX) { ... }
   }
   ```

5. **String concatenation in a loop** — replace with `StringBuilder` or `string.Join` when 3+ concatenations occur in a loop body.

---

## Task 1 — Helpers subsystem

**Files to read and modify (all in `ShokoRelay/Helpers/`):**
- `TagHelper.cs`
- `TextHelper.cs`
- `ContentRatingHelper.cs`
- `CastHelper.cs`
- `ImageHelper.cs`
- `OverrideHelper.cs`
- `ShokoExtensionHelper.cs`
- `MapHelper.cs`
- `LogHelper.cs`
- `TaskHelper.cs`

- [ ] **Step 1: Read all Helpers files**

Read each file listed above in full before making any changes.

- [ ] **Step 2: Apply simplification patterns**

Work through each file applying the simplification patterns from the Shared Rules section. Key things to watch for in this subsystem:

In `TagHelper.cs` — `GetFilteredTags` builds tag collections via LINQ; check for `Where`/`Select` ordering and unnecessary intermediate `.ToList()`.

In `TextHelper.cs` — `GetByLanguage` iterates over a language collection; check if `.ToList()` on `languages.Split(...)` can be avoided. `SummarySanitizer` chains multiple regex replaces — these are fine as-is (each replace feeds the next).

In `CastHelper.cs` — `GetCastAndCrew` and `GetStudioTags` use LINQ chains ending in `.ToArray()`. Check `Where`/`Select` ordering and whether anonymous objects used in Plex JSON serialization must remain (they must — do not convert those to tuples).

- [ ] **Step 3: Apply performance patterns**

Check each file for the performance patterns. Key areas:

`TagHelper.cs` — `GetFilteredTags` calls `userBlacklist.Contains(tagName, StringComparer.OrdinalIgnoreCase)` — if `userBlacklist` is a `string[]`, each `.Contains` is O(n). If called frequently consider noting it; do not restructure cross-file.

`CastHelper.cs` — String `.Equals` calls on `CrewRoleType` enum comparisons are fine (enum equality is already fast). Check string comparisons on names.

- [ ] **Step 4: Verify — build check**

Run from the repo root:
```
dotnet build ShokoRelay/ShokoRelay.csproj
```
Expected: `Build succeeded` with 0 errors. Fix any compilation error before proceeding.

- [ ] **Step 5: Commit**

```
git add ShokoRelay/Helpers/
git commit -m "perf: optimize Helpers subsystem"
```

---

## Task 2 — Plex subsystem

**Files to read and modify (all in `ShokoRelay/Plex/`):**
- `PlexApi.cs`
- `PlexAuth.cs`
- `PlexClient.cs`
- `PlexCollections.cs`
- `PlexHelper.cs`
- `PlexMapping.cs`
- `PlexMetadata.cs`
- `PlexConstants.cs`

- [ ] **Step 1: Read all Plex files**

Read each file listed above in full before making any changes.

- [ ] **Step 2: Apply simplification patterns**

Key areas to watch for:

`PlexClient.cs` — likely contains multiple HTTP request methods; check for `async` wrappers around single `await` calls that can collapse to direct `return task`. Only collapse if there is no surrounding `try/catch` or `using`.

`PlexMapping.cs` — likely maps Shoko metadata to Plex response objects using anonymous objects. Do NOT convert Plex-response anonymous objects to tuples — they serialize to JSON and the property names must remain as-is.

`PlexAuth.cs` — check for repeated property accesses and verbose null handling.

`PlexMetadata.cs` — likely the largest file; check for LINQ chain ordering and redundant `.ToList()` before `.Any()`.

- [ ] **Step 3: Apply performance patterns**

`PlexClient.cs` — if it builds URL strings via concatenation inside loops, check for `StringBuilder` opportunity. String interpolation for one-off URLs is fine.

`PlexCollections.cs` — check for repeated settings reads inside collection-building loops.

`PlexConstants.cs` — static data file; likely no performance issues, just check for `List<T>` constants that should be `FrozenSet` or `HashSet` for O(1) lookup if used in `.Contains` calls.

- [ ] **Step 4: Verify — build check**

```
dotnet build ShokoRelay/ShokoRelay.csproj
```
Expected: `Build succeeded` with 0 errors.

- [ ] **Step 5: Commit**

```
git add ShokoRelay/Plex/
git commit -m "perf: optimize Plex subsystem"
```

---

## Task 3 — VFS subsystem

**Files to read and modify (all in `ShokoRelay/Vfs/`):**
- `VfsBuilder.cs`
- `VfsAssetLinker.cs`
- `VfsWatcher.cs`
- `VfsShared.cs`
- `VfsHelper.cs`

- [ ] **Step 1: Read all VFS files**

Read each file listed above in full before making any changes.

- [ ] **Step 2: Apply simplification patterns**

`VfsBuilder.cs` is the largest file in this subsystem. Key areas:

- `BuildInternal` method — check for redundant `.ToList()` before `.Any()` and repeated `Settings` property accesses inside loops.
- The `GetSeasonSortKey` and `GetSeasonId` local functions inside `BuildInternal` — check for `StartsWith` calls missing `StringComparison`.
- Blueprint cache loading uses nested foreach loops — check for clarity improvements.

`VfsShared.cs` — check for `StringComparison` on path comparison helpers.

`VfsAssetLinker.cs` — check LINQ chains for ordering and redundant materialization.

- [ ] **Step 3: Apply performance patterns**

`VfsBuilder.cs`:
- Any `.Where(...).ToList()` before `.Any()` or `.Count()` — remove the `.ToList()`.
- `Settings.*` reads inside the parallel `Parallel.ForEach` / `Parallel.For` body — hoist above the parallel call.
- String comparisons using `.Equals` or `.Contains` without `StringComparison` — add `OrdinalIgnoreCase` for folder/path names.

`VfsWatcher.cs` — file system events; check for string comparisons on file paths.

- [ ] **Step 4: Verify — build check**

```
dotnet build ShokoRelay/ShokoRelay.csproj
```
Expected: `Build succeeded` with 0 errors.

- [ ] **Step 5: Commit**

```
git add ShokoRelay/Vfs/
git commit -m "perf: optimize VFS subsystem"
```

---

## Task 4 — Sync subsystem

**Files to read and modify (all in `ShokoRelay/Sync/`):**
- `SyncToPlex.cs`
- `SyncToShoko.cs`
- `SyncHelper.cs`

- [ ] **Step 1: Read all Sync files**

Read each file listed above in full before making any changes.

- [ ] **Step 2: Apply simplification patterns**

`SyncToPlex.cs`:
- Check for `async` wrappers around single `await` that can collapse.
- Check for repeated `Settings.Automation.*` reads inside the sync loop.
- Anonymous objects used in Plex metadata responses must NOT be converted to tuples.

`SyncToShoko.cs`:
- Check LINQ chain ordering for `Where`/`Select`.
- Check for redundant `.ToList()` on collections only used in `foreach`.

`SyncHelper.cs`:
- Likely a utility file; check for verbose null handling and unnecessary collection allocations.

- [ ] **Step 3: Apply performance patterns**

`SyncToPlex.cs`:
- `shokoWatchedRaw` filters — check if `.ToList()` on the initial result set can be deferred.
- String equality checks on library names — confirm `StringComparison.OrdinalIgnoreCase` is present.
- `matchedGlobal` is a `HashSet<int>` — already optimal. Confirm any other set/list lookups.

`SyncToShoko.cs`:
- Check for `List<T>.Contains` in inner loops — convert to `HashSet<T>` if found.

- [ ] **Step 4: Verify — build check**

```
dotnet build ShokoRelay/ShokoRelay.csproj
```
Expected: `Build succeeded` with 0 errors.

- [ ] **Step 5: Commit**

```
git add ShokoRelay/Sync/
git commit -m "perf: optimize Sync subsystem"
```

---

## Task 5 — Services & Controllers subsystem

**Files to read and modify:**

Services (`ShokoRelay/Services/`):
- `CollectionService.cs`
- `CriticRatingService.cs`
- `ImageSyncService.cs`
- `FfmpegService.cs`
- `ShokoImportService.cs`
- `SourceLinkService.cs`

AnimeThemes (`ShokoRelay/AnimeThemes/`):
- `AnimeThemesMp3Generator.cs`
- `AnimeThemesApi.cs`
- `AnimeThemesHelper.cs`
- `AnimeThemesMapping.cs`
- `AnimeThemesWebm.cs`

Controllers (`ShokoRelay/Controllers/`):
- `AnimeThemesController.cs`
- `BaseController.cs`
- `DashboardController.cs`
- `MetadataController.cs`
- `PlexController.cs`
- `ShokoController.cs`

Config & Root:
- `ShokoRelay/ShokoRelay.cs`
- `ShokoRelay/ShokoRelayConstants.cs`
- `ShokoRelay/Config/ConfigProvider.cs`
- `ShokoRelay/Config/RelayConfig.cs`
- `ShokoRelay/Config/PlexConfig.cs`

- [ ] **Step 1: Read all Services & Controllers files**

Read each file listed above in full before making any changes.

- [ ] **Step 2: Apply simplification patterns**

`CollectionService.cs`:
- Check LINQ chain ordering in collection-building loops.
- Check for `async` wrappers collapsible to direct `return`.

`CriticRatingService.cs` / `ImageSyncService.cs`:
- Check for repeated `Settings` reads inside processing loops.
- Check for `.ToList()` before `.Any()`.

Controllers:
- Controller action methods often have early returns — check for null-conditional opportunities.
- Check for `async` wrappers that wrap a single await with no surrounding logic.

`ShokoRelay.cs`:
- `AutomationLoop` already hoists `settings = Settings` outside the inner per-schedule blocks — verify this is applied consistently.
- `nextRuns.Any()` check: `nextRuns` is a `List<DateTime>` — `.Any()` on a populated list is fine; no change needed.

`ConfigProvider.cs`:
- Check for repeated file I/O or deserialization that could be cached.

- [ ] **Step 3: Apply performance patterns**

`CollectionService.cs` — parallel processing paths: confirm `Settings` properties used inside `Parallel.ForEachAsync` are hoisted to locals declared before the parallel call.

`AnimeThemesApi.cs` / `AnimeThemesHelper.cs` — check string comparisons in API response parsing; add `StringComparison` where missing.

`FfmpegService.cs` — check for string path construction; prefer `Path.Combine` over string interpolation for paths.

Controllers — HTTP response building: check for `.ToList()` before `.Any()` on result collections.

- [ ] **Step 4: Verify — build check**

```
dotnet build ShokoRelay/ShokoRelay.csproj
```
Expected: `Build succeeded` with 0 errors.

- [ ] **Step 5: Commit**

```
git add ShokoRelay/Services/ ShokoRelay/AnimeThemes/ ShokoRelay/Controllers/ ShokoRelay/ShokoRelay.cs ShokoRelay/ShokoRelayConstants.cs ShokoRelay/Config/
git commit -m "perf: optimize Services, Controllers, and AnimeThemes subsystems"
```

---

## Task 6 — Final build verification & cross-cutting review

This task runs after all 5 subsystem agents have completed and committed.

- [ ] **Step 1: Full clean build**

```
dotnet build ShokoRelay/ShokoRelay.csproj --configuration Release
```
Expected: `Build succeeded` with 0 errors, 0 warnings added beyond baseline.

- [ ] **Step 2: Check for REVIEW markers left by agents**

```
grep -rn "// REVIEW:" ShokoRelay/ --include="*.cs"
```

For each marker found, evaluate the flagged code and either apply the change or remove the marker with no change.

- [ ] **Step 3: Cross-cutting spot check**

Manually verify one representative file from each subsystem compiles and the change summary looks correct:
- `ShokoRelay/Helpers/TagHelper.cs`
- `ShokoRelay/Plex/PlexClient.cs`
- `ShokoRelay/Vfs/VfsBuilder.cs`
- `ShokoRelay/Sync/SyncToPlex.cs`
- `ShokoRelay/Services/CollectionService.cs`

- [ ] **Step 4: Final commit**

```
git add -A
git commit -m "chore: final build verification pass — resolve REVIEW markers"
```
