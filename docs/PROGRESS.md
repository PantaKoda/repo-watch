# Progress

Status values: `pending`, `in_progress`, `completed`, `blocked`. At most one stage is `in_progress`.

| Stage | Title | Status |
| --- | --- | --- |
| 01 | Project and development baseline | completed |
| 02 | Repository state and settings model | completed |
| 03 | Functional desktop shell | pending |
| 04 | Sign in with GitHub | pending |
| 05 | Repository access and watchlist picker | pending |
| 06 | Fetch and normalize real GitHub data | pending |
| 07 | Durable caching and efficient synchronization | pending |
| 08 | Modern visuals and real transparency | pending |
| 09 | Desktop behavior and notifications | pending |
| 10 | Near-real-time delivery (relay) | pending |
| 11 | Package and validate the Windows release | pending |
| 12 | Later macOS/Linux releases | pending |

## Stage 01 — Project and development baseline: completed

**Implemented**
- `RepoWatch.slnx` with `RepoWatch.Core`, `RepoWatch.GitHub`, `RepoWatch.Desktop` (Avalonia 12.1.3, `net10.0`), `RepoWatch.Core.Tests` and `RepoWatch.Desktop.Tests` (xunit v3 on Microsoft.Testing.Platform).
- `global.json` SDK pin, central package versions, committed NuGet lock files, nullable analysis, warnings as errors, deterministic builds (`ContinuousIntegrationBuild` and path mapping in CI).
- Centralized configuration: layered sources, strict binding, validation in Core, and an actionable error window with Open folder and Quit.
- Logging through Microsoft.Extensions.Logging, with debug output and a daily rolling file (7 files retained, 5 MB per-file cap).
- Windows app manifest (Windows 10/11, PerMonitorV2 DPI).
- CI: full build and tests on Windows; build plus Core tests on Ubuntu and macOS (build check only).
- README with platform baseline, commands and configuration reference; `docs/architecture.md`.

**Checks actually run (Windows 11 Pro 26200, .NET SDK 10.0.401)**
- `dotnet build RepoWatch.slnx` (Debug and Release): 0 warnings, 0 errors.
- `dotnet test --solution RepoWatch.slnx`: 21 passed.
- Clean-copy check: tracked files copied to an empty folder, then `dotnet restore --locked-mode` and a Release build succeeded.
- Launch smoke test of `RepoWatch.exe` with an isolated `REPOWATCH_DATA_DIR`. A valid configuration opened the "Repo Watch" window. An invalid `GitHub:ClientId` opened "Repo Watch — configuration problem". A log file was written.
  - This test found and fixed a bug: `REPOWATCH_DATA_DIR` collided with the configuration environment prefix. The configuration prefix is now `REPOWATCH__`, and a regression test covers it.

**Not verified / limitations**
- The CI workflow has not run on GitHub; the repository has no remote yet. macOS/Linux builds have only been defined, not run.
- The baseline window is a status placeholder. The widget UI arrives in Stage 03.
- No credentials exist or are committed. `GitHub:ClientId` is empty until a maintainer registers the GitHub App (Stage 04).

**Next concrete task** — see Stage 02.

## Stage 02 — Repository state and settings model: completed

**Implemented**
- **Identity** (`Core/Identity`): `AccountKey` (normalized host + numeric user ID) and `RepositoryKey` (account + repository ID) are the only storage keys. Owner/name live in `RepositoryMetadata` as display data.
- **Outcomes** (`Core/Status`): `CheckOutcome` keeps Unknown, Queued, Waiting, Running, Success, Failure, TimedOut, Cancelled, Skipped, Neutral, ActionRequired and Stale distinct. `CheckRollup` combines them into a state (`NoChecks` is separate from `Unknown`; skipped/neutral alone gives `Neutral`, not `Passing`) and keeps per-outcome counts.
- **Actions** (`Core/Actions`): `Workflow`, `WorkflowRun` (SHA, attempt, run number, event, PR numbers, URLs, timestamps) and `WorkflowJob`. `WorkflowRunSelection.ForCommit` ignores other commits, keeps the latest attempt per run (robust to late or out-of-order observations) and the newest run per workflow and event. `ActionsState` keeps default-branch health separate.
- **Pull requests** (`Core/PullRequests`): `PullRequest`, `ReviewRequest` (user/team), `PullRequestReview`, `MergeState` (Unknown is never "ready"), `CheckRun` and legacy `CommitStatus`. `CommitChecks.Summarize` handles superseded re-runs per app+name and the latest status per context. `ReviewSummary` follows GitHub's rules (comments don't override, dismissals clear, unsubmitted reviews are ignored) and flags approvals on older commits. In `PullRequestEntry`, checks and reviews are separate `Resource`s.
- **Issues** (`Core/Issues`): `Issue` and `IssuesState`. `ItemCount` marks partial counts ("30+").
- **Resource state** (`Core/State`): immutable `Resource<T>` with availability, last success, last attempt, last error and a cache flag. Failures keep the last value; `FeatureUnavailable` is an answer, not an error; `AccessLost` withholds cached content. `GetFreshness` returns NotLoaded/Fresh/Cached/Stale/Failed. `RepositorySnapshot` holds independent Metadata/Actions/PullRequests/Issues resources; `WithAccessLost` clears all of them.
- **GitHub mapping** (`GitHub/Mapping`): status/conclusion, commit status, review and mergeable-state strings map to domain values; unrecognized values become Unknown.
- **Settings** (`Core/Settings`): `AppSettings` (appearance incl. material/opacity/density/accent, window flags and per-display placements in DIPs, startup opt-ins, notification switches, quiet hours spanning midnight, active account, pause) and per-account `AccountSettings` (manual-order watchlist keyed by repository ID with branch/workflow filters, PR scope, issue and notification switches; ordering mode).
- **Schema version and migrations**: `SettingsCodec` stamps `schemaVersion`, runs a contiguous chain of JSON migrations, then normalizes (clamps opacity, drops invalid/duplicate entries, resets undefined enums). Unreadable data → defaults + `Corrupt`; newer schema → defaults + `NewerVersion`.
- **Persistence** (`Desktop/Storage`): `LocalDatabase` (SQLite, WAL, ordered migrations tracked by `PRAGMA user_version`, rejects databases from newer versions) and `SqliteSettingsStore` (one JSON document per scope `app` / `account:<host>/<id>`; unreadable documents go to `settings_backup` first; newer-schema documents are never overwritten). Registered in DI; the database is created on first use.

**Checks actually run (Windows 11, .NET SDK 10.0.401)**
- `dotnet test --solution RepoWatch.slnx`: 91 passed (70 new) at first review; 105 after review fixes. Covers current-commit aggregation, superseded/out-of-order attempts, rerun checks, review rules, independent resource failures, access loss, settings round trips through the codec and through SQLite across a simulated restart, account isolation, corrupt backup, newer-version protection, migration chaining and database version rejection.
- `dotnet restore --locked-mode` and `dotnet build -c Release`: 0 warnings, 0 errors.
- Release `RepoWatch.exe` launched with an isolated data folder and opened its window.

**Review fixes (PR #1)**
- Check runs are grouped by check suite + name, not app + name. Same-named jobs in different workflows no longer collapse and hide a failure.
- Cached data becomes `Stale` after a failed refresh or past the stale threshold. `Cached` now means recent and not yet confirmed.
- Settings loading drops individual invalid values by JSON path (`Repaired`) instead of resetting the whole document. Watchlist owner/name are optional display data.
- Unknown settings properties are preserved through `[JsonExtensionData]`. Adding enum members or changing types now requires a version bump.
- Database migrations use `BEGIN IMMEDIATE` and re-read the version inside the transaction. WAL is enabled only after the version check.
- `WorkflowRunSelection` groups by workflow + event + branch and takes an optional branch filter for default-branch health.
- `AccountKey` rejects URL-form hosts.
- Regression tests were added for each fix (105 tests total). The concurrent-initialization stress test did not reproduce the original race against the old code on this machine, so it guards against regressions rather than proving the race.

**Not verified / limitations**
- The running app does not read or write settings yet; nothing in the UI consumes them until Stage 03. Store behavior is verified only by tests.
- Window placement is modeled but not applied (Stage 03). Cached snapshots, ETags and notification history are not persisted yet (Stage 07).
- No real GitHub data; models are exercised with synthetic fixtures only.

**Next concrete task**
- Stage 03: build the widget window (compact bar + expanded details, Actions/PRs/Issues tabs, freshness indicators, manual refresh, empty/loading/error states), a settings/onboarding window, tray menu (Show/Settings/Quit) with window recovery, drag/resize/always-on-top, and per-display placement using `AppSettings`. Use clearly labeled demo fixtures in an explicit demo mode only.
