# Progress

Status values: `pending`, `in_progress`, `completed`, `blocked`. At most one stage is `in_progress`.

| Stage | Title | Status |
| --- | --- | --- |
| 01 | Project and development baseline | completed |
| 02 | Repository state and settings model | completed |
| 03 | Functional desktop shell | completed |
| 04 | Sign in with GitHub | completed |
| 05 | Repository access and watchlist picker | completed |
| 06 | Fetch and normalize real GitHub data | completed |
| 07 | Durable caching and efficient synchronization | completed |
| 08 | Modern visuals and real transparency | completed |
| 09 | Desktop behavior and notifications | completed |
| 10 | Near-real-time delivery (relay) | completed |
| 11 | Package and validate the Windows release | completed |
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

**Next concrete task** — see Stage 03.
- (Original plan) Stage 03: build the widget window (compact bar + expanded details, Actions/PRs/Issues tabs, freshness indicators, manual refresh, empty/loading/error states), a settings/onboarding window, tray menu (Show/Settings/Quit) with window recovery, drag/resize/always-on-top, and per-display placement using `AppSettings`. Use clearly labeled demo fixtures in an explicit demo mode only.

## Stage 03 — Functional desktop shell: completed

**Implemented**
- **Widget** (`Views/WidgetWindow`): borderless window, 400 DIP wide by default (minimum 320 × 180). Parts:
  - Draggable header with a connection badge (Demo data / Not signed in / Polling / Live / Offline / Paused / Reconnect required). Refresh (F5), Expand/collapse and Settings (Ctrl+,) buttons appear only when there is something to act on; Hide is always shown.
  - Compact repository list and expanded details with Actions / Pull requests / Issues tabs.
  - Per-section freshness line (Updated / Cached / Stale + error / Couldn't load). Loading, empty, feature-unavailable and access-lost messages.
  - Footer summary and a resize grip.
  - Every row opens GitHub through the validated browser adapter. Status dots always have a text label.
- **States:** signed-out ("Explore demo data" / Settings), empty watchlist ("Add repositories"), and labeled demo mode (`--demo` or the button) with a permanent "Demo data: sample repositories, not from GitHub" banner and Exit demo.
- **Demo fixtures** (`Demo/DemoRepositoryMonitor`): five synthetic repositories covering failing, running→success progression, no checks, issues turned off, stale PRs after a server error, archived, private and access lost. Simulated refresh latency, 20 s auto-refresh. No network calls.
- **Keyboard:** Tab reaches the list, arrows move, Enter/Space opens details, Tab/Enter activate rows, Esc goes back from details then collapses. Focus returns to the selected row.
- **List stability:** `CollectionReconciler` updates rows in place. While the pointer is over the body or keyboard focus is inside it, rows keep their positions (new rows append); the attention-first order is applied with moves afterwards, preserving the row instances, selection and focus.
- **Window behavior:** drag (respects Lock position), resize grip, optional always-on-top, no focus stealing on data updates.
- **Placement:** `WindowPlacementService` stores placement per display configuration (`PlacementPolicy.DisplayKey`), with size in DIPs and position in virtual-screen pixels. It restores on start, saves after moves/resizes (debounced), and on display changes re-applies the matching placement or pulls an unreachable window back to the primary work area.
- **Tray** (`Platform/Tray`): icon with Show widget / Settings… / Quit; click toggles the widget. With a tray, closing or hiding the widget keeps the app running and the widget is not in the taskbar. Without one (Linux for now, or if creation fails), the widget stays in the taskbar, Hide minimizes and closing exits.
- **Settings window:** always-on-top, lock position, theme (System/Light/Dark, applied live), demo mode, data folder, version, Quit, and storage problems. Sign-in and repository selection are explained as not yet available rather than shown as dead controls.
- **Core policies:** `IRepositoryMonitor`/`ConnectionState`, `AttentionPolicy` (Failure > Warning > Active > Quiet, manual order within a level), `PlacementPolicy`, `ExternalLinkPolicy` (HTTPS on the GitHub host and its subdomains only).
- Settings persistence is now live: `SettingsService` loads at startup, saves 500 ms after a change and flushes on exit. If storage is unavailable it continues in memory and reports why.

**Checks actually run (Windows 11 Pro 26200, .NET SDK 10.0.401)**
- `dotnet test --solution RepoWatch.slnx`: 138 passed. New tests:
  - placement geometry and attention ordering
  - link policy
  - reconciler stability
  - widget view-model: deferred reorder keeps the focused row, empty states, details navigation, explicit demo
  - headless UI (Avalonia.Headless + Skia): 320/400 px light/dark rendering with the demo label, no horizontal text overflow at 320 px, all three tabs, stale/unavailable states, full keyboard path, signed-out state hides inert buttons, settings controls change settings
- Screenshots: `artifacts/screenshots/stage03/` (not committed). Reviewed: compact 320 light/dark, 400 light, details for each tab, stale PRs, signed-out, settings.
- Real Windows smoke test (`RepoWatch.exe`, isolated data folder):
  1. First launch is placed at the top-right of the work area (400 × 520).
  2. After a move and restart, the window reopens at the same position.
  3. Moved to (-9000, -9000) and restarted, it returns to the primary work area.
  4. WM_CLOSE hides the window and the process keeps running (tray mode).
  5. The signed-out launch renders.

  Real desktop screenshots were captured and match the headless output. The log reports "tray available".
- Release build and `--locked-mode` restore: 0 warnings, 0 errors.

**Review fixes (PR #2)**
- The periodic tick re-evaluates freshness, so data that stops arriving turns `Stale` without a monitor event.
- `SettingsService` serializes snapshot and write, so an older snapshot is never saved last. `Dispose` waits for a running timer callback. A failed save keeps the change pending. A test confirms the ordering test fails without the fix.
- `AppChanged` carries the previous and current settings. The widget, settings window and theme react only to the fields they use, so placement saves while dragging no longer rebuild the widget.
- Save failures raise `ProblemChanged`, which an open settings window shows.
- `OpenDataFolderAsync` returns a result and catches all exceptions; a failure is shown in settings.
- Tray click hides the widget only if it was just in use (active, or deactivated by that click within 600 ms); a covered widget is brought forward instead.
- `IExternalBrowser` returns `Opened`/`Refused`/`Failed` and never throws. The widget footer shows a short notice when a link is refused or the browser can't start.
- Relative minutes truncate ("59m ago", not "60m ago").
- 149 tests passing. The tray-click heuristic is not automated.

**Not verified / limitations**
- Tray menu clicks (Show/Settings/Quit) and recovery by clicking the tray icon were not automated. Tray creation is confirmed only from the log. Please check manually: hide the widget, then click the tray icon.
- Display-change handling (`Screens.Changed`) is covered by the geometry tests and the off-screen restart, but no monitor was actually unplugged.
- Placement saves are debounced, so a crash within ~1 s of a move loses that move.
- xunit.v3 is held at 3.2.2 because Avalonia.Headless.XUnit 12.1.3 fails test discovery on xunit.v3 4.x.
- Ordering is always attention-first until per-account settings arrive (Stage 05). Single-instance activation and a global Show/Hide shortcut are Stage 09.
- The icon is drawn at runtime; a designed `.ico` comes with packaging (Stage 11).

**Next concrete task**
- Stage 04: GitHub App device-flow sign-in. Needs a GitHub App registered with device flow enabled (maintainer task) and its client ID in `GitHub:ClientId` for live verification. Implement the device-code/poll states with contract tests first, a Windows Credential Manager token store behind `ICredentialStore`, refresh-token rotation, identity resolution and sign-out cleanup.

## Stage 04 — Sign in with GitHub: completed

**GitHub App:** "Repo Watch PantaKoda", registered by the maintainer (3 Oct 2026) with device flow enabled. Its public client ID `Iv23liDwna1lEXO9Nlh3` is in the shipped `appsettings.json`. The app slug `repo-watch-pantakoda` is in `GitHub:AppSlug`, so the installation link is `https://github.com/apps/repo-watch-pantakoda/installations/new`. The public apps API returns 404 for this slug, which suggests the app is installable only on the owner's account. Other users and organizations would need it switched to "Any account".

**Live verification against github.com (3 Oct 2026, Windows 11, real Repo Watch services, throwaway data folder and Credential Manager prefix, no tokens printed):**
1. Device-flow sign-in, approved on a phone: signed in as PantaKoda, keyed `github.com/147987379`. Tokens were stored in Credential Manager (access token expires after 8 h, refresh token after 6 months). The active account and last login were saved.
2. Restart: the session was restored from Credential Manager and the identity re-confirmed through `GET /user`.
3. Forced renewal: with the stored access token marked expired, the restart refreshed it **without a client secret**. Both tokens rotated and the new pair was persisted.
4. Reusing the rotated refresh token was rejected by GitHub with **`incorrect_client_credentials`** (not `bad_refresh_token`). The session treats any refresh error as reconnect-required; this response is now a contract test.
5. Sign-out removed the credential (none left in Credential Manager) and cleared the active account.

Also observed live: an unknown client ID gets 404 `{"error":"Not Found"}` from `/login/device/code`. A poll with the real client ID returned `authorization_pending`, confirming device flow is enabled.

**Review fixes (PR #3)**
- **Renewal belongs to the session, not a caller.** The single-use refresh runs on the session lifetime; callers stop waiting with `WaitAsync`. A caller that cancels mid-refresh can no longer lose the rotated tokens.
- If saving rotated tokens fails, they are still adopted in memory (the old refresh token is already dead) and the settings show a storage warning. The exception no longer escapes and leaves the account stuck in "Restoring".
- **No write after sign-out.** The session state is checked before writing, and `CloseAsync` waits for an in-flight renewal; sign-out awaits it before deleting the credential.
- There is no semaphore or CTS to dispose under in-flight work, so `ObjectDisposedException` can no longer occur.
- Credential removal always also targets the original secure store, so a session-only fallback can't leave the previous account's tokens in Credential Manager.
- A 401 after a successful renewal now goes through `RequireReconnectAsync`: tokens are deleted and the session cancelled, rather than a reconnect message over an active session.
- **Accurate connection states:** `Connecting` while restoring and `Signed in` (`SignedInIdle`) when nothing is monitored. "Polling" is shown only once real monitoring exists (Stage 06). The widget shows a *Connecting to GitHub…* panel.
- **The sign-in flow is owned by `AccountService`.** Closing settings (Esc) no longer cancels it, and reopening shows it. Sign-out and quitting still cancel it.
- The avatar download is bounded even without a Content-Length.
- Regression tests: 4 renewal-race tests (caller cancellation, store failure, close during renewal, dispose during renewal) and 5 account tests. 197 tests passing. The renewal changes have not been re-run live; that needs another phone approval.

**Verified only with fixtures:** denial on GitHub's approval page, natural device-code expiry, revocation through GitHub's authorization settings, and the full app UI flow. The UI is covered by headless tests, but the live check used the services directly. These can be checked with the checklist in `docs/github-app-setup.md`.

**Implemented**
- `docs/github-app-setup.md`: registration settings (device flow on, expiring tokens on, no callback, no webhook yet), least-privilege read permissions, the client ID versus App ID distinction, and no client secret or private key in the desktop app. Includes the user flow and a verification checklist.
- **Device flow** (`GitHub/Auth/DeviceFlowClient`, `DeviceFlowSignIn`):
  - Endpoints and fields verified against GitHub docs (October 2026). Only the public `client_id` is sent.
  - Polling honors `interval`, adds 5 s on `slow_down` (or takes GitHub's new interval), stops at local expiry without another request, and stops immediately on cancel.
  - Transient failures keep polling until the code expires.
  - `access_denied`, `expired_token`, `device_flow_disabled` and other errors become explicit outcomes.
- **Identity** (`GitHub/Users/GitHubUserClient`): `GET /user` with REST API version `2026-03-10`. The account key is the host plus the numeric user ID; login and avatar are display data. A token mismatch, 401 or outage is reported distinctly.
- **Renewal** (`GitHub/Auth/AccountSession`):
  - Renews 5 minutes before expiry with no client secret (documented for device-flow tokens). One refresh at a time per account.
  - Refresh tokens are single-use, so the rotated pair is persisted before use.
  - A rejected refresh (or a 401 that a refresh can't fix) deletes the dead tokens and moves to `ReconnectRequired`, with no retry loop.
  - A transient failure keeps a still-valid token; otherwise it reports `TokenUnavailable`, which shows as Offline.
- **Secure storage:** `WindowsCredentialStore` keeps a generic credential per account (`RepoWatch:github/<host>/<id>`) for the current user on this machine, not roaming, and zeroes buffers. On other platforms, or if a write fails, `SessionCredentialStore` keeps tokens in memory only and the UI says "Session only". There is never a plaintext fallback, and tokens are never in SQLite, settings, logs, URLs or the clipboard. `StoredCredential`/`DeviceAuthorization` `ToString` are redacted.
- **Account lifecycle** (`Desktop/Services/AccountService`): restores at startup (stored credentials, then renewal if needed, then identity confirmation). The resulting states are SignedIn, Offline (GitHub unreachable, credentials kept) or ReconnectRequired (missing, rejected or mismatched credentials). The completed sign-in persists the active account and last known login. One active account; signing in as another account removes the previous credentials.
- **Sign-out:** cancels the session lifetime (in-flight work), deletes local credentials, clears the active account and keeps the watchlist preferences. No private repository content is cached yet; the Stage 07 cache must be cleared here (marked in code).
- **UI:**
  - The settings Account section shows: Sign in with GitHub; the large selectable user code with Copy code, Open GitHub (also opened automatically), live status (waiting, slowed down, retrying), expiry countdown and Cancel; outcome messages (declined, expired, not configured, unknown client ID); the signed-in login, name and avatar; where the tokens are stored; Sign out; Review access on GitHub; and a reconnect panel.
  - The widget shows "Sign in with GitHub" when configured, an explicit not-configured message otherwise, and a *Reconnect to GitHub* state.
  - The demo overlay returns to the account's state when exited.

**Checks actually run (Windows 11 Pro 26200, .NET SDK 10.0.401)**
- `dotnet test --solution RepoWatch.slnx`: 187 passed, 0 skipped.
  - New `RepoWatch.GitHub.Tests` (22): device-code request contents, poll interval and `slow_down` timing (fake clock), every terminal error, local expiry with no extra request, cancellation, transient retries, refresh request contents, rotation persisted, a single refresh for 20 concurrent callers, rejected refresh leading to reconnect with no retries, transient handling, 401 handling, identity request headers, redaction.
  - Desktop (17 new): end-to-end sign-in, restart restore, denied/expired/disabled/cancelled change nothing, expired access token renewed at startup, revoked access leading to reconnect and no further requests, outage leading to Offline, sign-out cleanup that keeps the watchlist, account switch, secure-storage failure leading to session-only.
  - Headless UI: the code panel (device code never visible, Copy/Cancel work), the signed-in panel, the widget reconnect state at 320 px.
- **Real Windows Credential Manager round trip:** write, read, delete, double delete, under a throwaway target prefix.
- **Real GitHub contract check:** `POST https://github.com/login/device/code` with a deliberately invalid client ID returned **404 `{"error":"Not Found"}`**, not the documented `incorrect_client_credentials`. It is now mapped to "GitHub didn't recognise this app's client ID" and covered by a contract test.
- Screenshots: `artifacts/screenshots/stage04/` (signing in, signed in, widget reconnect).

**Not verified / limitations**
- Denial, natural expiry and revocation through GitHub settings have not been exercised live (see above).
- Offline at startup does not retry automatically yet. *Try again* in settings re-runs the restore, and network-recovery refresh comes in Stage 07.
- The widget's "Add repositories" leads to settings, where repository selection arrives in Stage 05.

**Next concrete task**
- Stage 05. List installations and their repositories through all pages (`GET /user/installations`, `GET /user/installations/{id}/repositories`), build the picker, persist the watchlist per account, and distinguish "not granted" from "not selected". Use contract fixtures until a client ID exists.

## Stage 05 — Repository access and watchlist picker: completed

**Implemented**
- **GitHub API foundation** (`GitHub/Api/GitHubApiClient`):
  - Authenticated GETs (API version 2026-03-10, token only in the header).
  - One renewal and retry on 401 through `IAccessTokenSource` (`AccountSession` implements it).
  - Pagination follows `Link: rel="next"` as given, **only on the API host**, at `per_page=100`. A page limit marks lists incomplete instead of presenting a first page as the total.
  - Error classification: Unauthorized, Forbidden, **SsoRequired** (with the SSO authorization URL from `X-GitHub-SSO`), **RateLimited** (with `RetryAt` from `Retry-After`/`x-ratelimit-reset`), NotFound, server and network errors.
- **Access discovery** (`GitHub/Access/AccessCatalogClient`):
  - `GET /user/installations` and `GET /user/installations/{id}/repositories`, every page.
  - Combines personal and organization installations, de-duplicates by repository ID, and keeps owner/name, privacy, organization and archived status.
  - Suspended installations are reported, not queried. SSO and 403 failures are kept per installation, and missing read permissions are listed.
  - Workflows are listed on demand (`GET /repos/{owner}/{repo}/actions/workflows`) for per-repository filters.
- **Core** (`Core/Access`):
  - `Watchlist` edits by repository ID: add keeps order, remove, move, update, and name refresh after renames or transfers.
  - `PickerFilter`: search across name and description, owner filter, selected-only.
  - `AccessClassifier`: Granted, NotGranted (only when the list is complete and the owner's installation is healthy), Suspended, SsoRequired, or Unknown. It never claims a repository doesn't exist.
- **Desktop services:**
  - `WatchlistService`: per-account watchlist and ordering, saved immediately, change events listing removed IDs.
  - `AccessCatalogService`: loaded only on request (picker, onboarding, Refresh list), shared in-flight load, discarded on account change; refreshes watched names after a load.
  - `MonitorCoordinator`: builds the widget's monitor from the account state and watchlist. Only watched repositories are handed to a monitor. A removal replaces the monitor immediately, and redundant rebuilds are skipped.
  - `WatchlistMonitor`: shows watched repositories in the user's order, "not loaded", with no requests. Stage 06 replaces it with real data.
- **UI:**
  - Repository manager window with three tabs:
    - **Watched:** ordering mode (attention first / my order), Move up/down, Remove (widget only), per-repository access status with Manage access, and options (PR scope Mine/All/None, show issues, notifications, branches, workflow selection loaded from GitHub).
    - **Add repositories:** search, owner filter, Show selected, Clear filters, Refresh list, per-row checkboxes with privacy/organization/archived badges, a selected count, and bulk *Add/Remove N shown* whose text states whether it applies to the filtered results or the whole list. Grant-access and empty states.
    - **Access:** installations with kind, scope and health (suspended, SSO with *Authorize single sign-on*, couldn't list, missing permissions), plus Manage access, Grant access, Refresh list, and notes on pending organization approval and on removal not uninstalling.
  - **Onboarding window** (first run when signed out): Sign in → Grant repository access (shows existing installations so users can continue) → Choose repositories → Appearance (theme, always on top) → Open widget. The sign-in UI is shared with settings (`AccountView`).
  - Settings has a Repositories summary and *Manage repositories…*. The widget's empty watchlist shows *Add repositories*.

**Checks actually run (Windows 11, .NET SDK 10.0.401)**
- `dotnet test --solution RepoWatch.slnx`: 228 passed. New coverage:
  - GitHub contract tests: multi-page installations and repositories, de-duplication, private/organization/archived mapping, no off-host pagination, the page limit giving incomplete results, suspended installations not queried, SSO URL captured, missing permissions, rate-limit reset, one 401 retry and its failure, workflows paging.
  - Core: watchlist edits, renames, access classification, picker filters.
  - Desktop acceptance: catalog mapping; granted access not auto-selected; the watchlist surviving restart in order and per account; only watched repositories monitored; immediate removal; filter and bulk wording; grant-access and empty states; the watchlist editor; rename refresh; the whole onboarding flow; the widget's Add repositories.
- Headless renders (`artifacts/screenshots/stage05/`): each manager tab, an Access tab with suspended and failing installations, and all four onboarding steps.
- Release launch with a fresh data folder: the onboarding window "Set up Repo Watch" opens with the widget.
- Found and fixed while testing: the picker compared the catalog time with the system clock instead of `TimeProvider`; the coordinator rebuilt the monitor twice per account change; "1 repositories" wording.

**Live verification against github.com (3 Oct 2026, real services, throwaway data folder and credential prefix, no tokens printed)**
1. Signed in as PantaKoda (approved on a phone).
2. Catalog: one installation (personal account, *All repositories*, healthy, no missing permissions), **97 repositories including 60 private**. The list was complete.
3. Pagination:
   - At `per_page=10`, 10 real pages were followed through `Link` headers, giving 97 items, complete, and the **same set** as the normal listing.
   - An earlier run at `per_page=1` followed 50 real pages and then stopped at the safety limit, correctly reporting **incomplete** rather than a partial total.
4. Watched two private repositories; after a restart both were still watched, in order.
5. Sign-out removed the credential (none left in Credential Manager) and kept the 2 watchlist entries for the next sign-in.

**Review fixes (PR #4)**
- **Picker rows are reused by repository ID** across catalog refreshes, so the visible checkboxes always follow the watchlist after *Refresh list*, *Add all* or a removal on the Watched tab, and renamed repositories are added under their new name.
- `X-GitHub-SSO: partial-results` on **successful** responses is now honored. `PagedList.SsoPartial` records it with any organization IDs it names. The catalog marks `SsoHidesInstallations`, or `InstallationAccess.SsoPartial` per installation, and is incomplete. Affected watched repositories show "access couldn't be confirmed … authorize SSO", never "not granted". The Access tab explains it.
- `TokenUnavailableException` from token renewal, including after a 401, is converted to a `Network` `ApiResult` in `GitHubApiClient`, so *Choose workflows…* can't crash the app.
- Workflow listing runs on the session lifetime (sign-out cancels it and gives a result, never an exception), uses the repository's current name from the catalog by ID, and discards results if the account changed or the repository was removed meanwhile.
- A rate-limited repository listing stops the loop: the remaining installations are reported as unavailable with the same `RetryAt`.
- Reconnect no longer reopens full onboarding; only a first-time user (onboarding never finished and no account) gets it. Onboarding opened while signed in loads existing access immediately.
- Link buttons in the picker, the Access tab and the watchlist editor show a notice when a link is refused or the browser can't start (`LinkNotice`).
- The picker no longer says "GitHub no longer lists"; it says "no longer grants" only for a complete list and "access couldn't be confirmed" otherwise.
- Unsaved Branches text survives other option changes.
- `MonitorCoordinator` updates the current monitor in place for option, order, name and addition changes, and rebuilds only on account/state changes or removals (`IWatchlistAwareMonitor`). In Stage 06, toggling an option won't restart polling, and removal still stops work immediately.
- 15 regression tests; 243 tests passing.

**Not verified live**
- Organization repositories, suspended installations, SSO (including partial results) and pending organization approval: the maintainer account has no organization installation. These are covered by contract fixtures only. That GitHub App user tokens receive `X-GitHub-SSO: partial-results` is documented for tokens generally but not confirmed for this app.
- Installing or changing access through the app's links was done by the maintainer on GitHub, not through Repo Watch's buttons.

**Next concrete task**
- Stage 06 (done below).

## Stage 06 — Fetch and normalize real GitHub data: completed

**Implemented**
- `RepositoryDataClient` (GitHub project), read-only:
  - **Metadata:** `GET /repositories/{id}`. Loading by ID follows renames and transfers.
  - **Actions (REST):**
    - Recent runs: `GET /actions/runs?per_page=20`.
    - Per tracked branch: the head commit from `GET /commits/{branch}/status`, then that commit's runs from `GET /actions/runs?head_sha=`.
    - Current-commit selection keeps the latest attempt per run, the newest run per workflow, event and branch, and that branch only.
    - Workflow filters apply to both the recent runs and branch health.
    - A 404 on the run list means Actions is unavailable. A missing branch is skipped.
  - **Pull requests:** one GraphQL query per repository returns reviews, pending review requests and `mergeable`.
    - **All:** the 30 most recently updated open PRs, with an exact `totalCount`.
    - **Mine:** two GraphQL searches, `author:me` and `review-requested:me`, deduplicated, so older PRs aren't missed. The count is exact only when both searches returned everything; otherwise it is a lower bound ("1+").
    - **Checks:** loaded over REST (`/commits/{sha}/check-runs` + `/status`) for the 10 most recent listed PRs. A live run showed that GraphQL commit data (`statusCheckRollup`) is denied without Contents access.
  - **Pull request rules:**
    - Reviews, checks and mergeability stay separate.
    - An incomplete check list never rolls up to Passing.
    - A GraphQL error for one PR (by path) fails only that PR's reviews; an error not tied to a PR fails all reviews. A denied or failed check request shows "couldn't load", never "No checks".
    - "Mine" means authored by me, or my review requested directly. Team requests don't count, because membership isn't established.
    - Nothing is shown as "ready to merge".
    - Partial lists are labeled ("showing 30 most recent of 45", "more may exist"). Rate-limited sections say when polling resumes.
  - **Issues:** one GraphQL query returns the 20 most recently updated open issues, an exact `totalCount` (which excludes pull requests), and `hasIssuesEnabled`.
  - **Errors:** GraphQL errors are classified (NOT_FOUND, FORBIDDEN, RATE_LIMITED). A GraphQL rate limit uses the `x-ratelimit-reset` header. A connection reset while reading a body (`IOException`) is a network error. Messages never echo repository names. `GitHubApiClient` gained `PostGraphQLAsync`.
  - **Missing branches:** a missing tracked branch is listed (`release: not found`). It is never silently replaced as the primary branch.
- `PollingRepositoryMonitor` (Desktop) replaces the placeholder `WatchlistMonitor` whenever an account is signed in and its watchlist is non-empty (`IRepositoryMonitorFactory` → `GitHubMonitorFactory`).
  - **Loop:** one background loop refreshes one repository at a time.
  - **Intervals:** active work 20 s, normal 90 s, after a failure 60 s. Manual refresh and option changes refresh at once. A result for superseded options is discarded.
  - **Rate limits:** pause all polling until the reset (at most 1 h). A manual refresh during the pause answers at once.
  - **Resilience:** an unexpected exception in one refresh is logged and recorded on that repository (values kept), and polling continues.
  - **Access restored:** sections lose their "access lost" state as soon as metadata loads again, so a later failure shows its own error.
  - **Connection state:** Offline when every repository's last refresh failed on the network; otherwise Polling.
  - **Lifetime:** runs on the session `Lifetime`. Sign-out, account change or a removal disposes it.
- `RepositoryRefresh`: metadata first. 404/403/SSO on the repository means access lost, and every section's cached content is withheld. Otherwise Actions, pull requests and issues load and fail independently, each keeping its last good value. Pull requests set to "None" and issues turned off (in Repo Watch or on GitHub) make no request.
- **Multi-branch health:** `ActionsState.Branches` holds every tracked branch's health. Attention considers all of them. The row shows `main: Passing · release: Failing`.

**Checks run**
- `dotnet test --solution RepoWatch.slnx`: 282 passed before the review fixes (296 after).
- 13 new HTTP-contract tests (`RepositoryDataTests`):
  - metadata by ID, and a lost repository;
  - branch head and latest attempt;
  - workflow filters;
  - missing branch, and disabled Actions;
  - a failing branch lookup;
  - pull requests: the GraphQL request shape, re-run supersession, legacy status, reviews on older commits, team requests, the "Mine" filter, incomplete checks and draft PRs;
  - GraphQL NOT_FOUND;
  - exact issue counts, a ghost author and disabled issues;
  - GraphQL rate limits.
- 11 monitor tests (`PollingMonitorTests`, `CoordinatorPollingTests`):
  - section independence and keeping the last good value;
  - access loss;
  - network failure → Offline with cached values;
  - turned-off sections make no requests;
  - only watched repositories are requested, and removed ones stop;
  - superseded options are discarded;
  - the rate-limit pause;
  - dispose stops requests;
  - active repositories refresh sooner;
  - the coordinator starts polling only for a non-empty watchlist, applies options in place, and rebuilds on removal or sign-out.
- **Live smoke check** against github.com (PantaKoda, 97 accessible repositories, approved by device flow on the maintainer's phone). The harness scanned every repository and refreshed three private ones exactly as the app does: hexDumper (failed runs), DataBaseModels (one open PR) and 24go. It then compared the results with plain REST:
  - open PRs from `/pulls`: matched;
  - open issues from `/issues` minus pull requests: matched. DataBaseModels' REST issue list contains its PR, and Repo Watch correctly counts 0 issues.
  - The first live run found that `GET /git/ref/heads/{branch}` and `/branches/{branch}` return 403 without Contents access. Branch heads now come from `/commits/{branch}/status`; the probe confirmed 200 for that endpoint and 404 for a missing branch.
  - After the fix, hexDumper showed its 6 recent runs including 2 failed. Its `master` head commit has no runs, so it shows "No checks", not the older failure.
  - The real app (Debug build, frosted dark) restored the session and showed the three repositories with real data and a "Polling" badge. Screenshots are in `artifacts/screenshots/stage06/` (not committed).
  - The test session was signed out afterwards: the credential was removed and the data folder deleted.

**Review of PR #6 (all seven findings fixed)**
- Polling survives unexpected exceptions; body read resets are network errors.
- "Mine" uses search, with honest counts.
- GraphQL partial errors are mapped per PR.
- Manual refresh no longer waits during a rate-limit pause.
- A missing primary branch is explicit.
- "No access" is cleared once access returns.
- GraphQL rate limits use the reset header.
- Each fix has a regression test. `dotnet test`: 296 passed.
- **Live re-check:** the new "Mine" search and the "All" query both returned DataBaseModels #1 with its reviews. The same run proved the reviewer's point: `statusCheckRollup` came back as a FORBIDDEN partial error, which the old code would have shown as "No checks". Checks now use REST endpoints already confirmed live (`check-runs` and `status` return 200 with this app's permissions). A live run of the REST check path on a PR is still pending; it needs another device-flow approval.

**Remaining limitations**
- **Workflow jobs:** not fetched yet. No UI shows them; add them when run rows can expand.
- **PR checks:** loaded for the 10 most recent listed PRs only (two REST requests each); the rest show "Checks: not loaded".
- **GitHub search:** results can lag the live state by a short time, so a new PR may appear in "Mine" slightly later.
- **Live coverage:**
  - None of the accessible repositories has open issues, so live issue counts were verified only at 0. Contract fixtures cover non-zero counts.
  - No accessible open PR has check runs, so live PR checks have not been seen with real results.
  - Organization, SSO and team paths are fixture-only.
- **Pull request window:** "Mine" and the items list consider the 30 most recently updated open PRs. The count is always the exact total.
- **Caching:** delivered in Stage 07.

**Next concrete task**
- Stage 07 (done below).

## Stage 07 — Durable caching and efficient synchronization: completed

**Implemented**
- **One scheduler per account** (`PollingRepositoryMonitor`). A single loop refreshes one repository at a time.
  - Metadata, Actions, pull requests and issues each have their own due time. Duplicate requests (manual refresh, option edits, focus, wake) coalesce into those due times. Parts due within 10 s are batched into one pass.
  - Priority goes to the repository whose details are open (`IRepositoryMonitor.SetFocus`), then to repositories with active runs, then to the earliest due.
  - A wake-up version counter guarantees that a signal arriving while the loop decides is never lost (found by a parallel test run).
- **Intervals** (`PollingPolicy`, from `Polling` options):
  - Actions: 20 s while running or focused, otherwise 180 s.
  - Pull requests: 90 s (20 s when focused). Issues: 120 s (90 s when focused). Metadata: 180 s.
  - **Slowdown:** hidden widget ×3, battery ×2, low request budget ×2, at most ×8 and at most 1 h.
  - **Failures:** bounded exponential backoff with ±20% jitter (60 s × 2ⁿ, at most 15 min). 404/403/SSO are answers, not failures.
  - **Rate limits** pause everything until the reset (`RetryAt` from Retry-After or `x-ratelimit-reset`, also for GraphQL).
  - `RateBudget` tracks `x-ratelimit-remaining/limit/reset` per resource (core, graphql) and slows polling below 10% left.
  - `X-Poll-Interval` is only sent by endpoints Repo Watch doesn't use (events, notifications), so it isn't consumed.
- **ETag conditional requests:** single REST GETs (metadata, run lists, branch status, check runs) send `If-None-Match`. A 304 reuses the cached body. A new 200 with an ETag replaces the entry. Errors and GraphQL are never cached.
- **SQLite caches** (migration 2, `RepositoryCache`), keyed by account:
  - `repository_snapshots` holds the last good data per watched repository, in a versioned JSON format; unreadable or mismatched rows are discarded.
  - `http_cache` holds ETag plus body.
  - At start, cached snapshots appear at once labeled "Cached", so there is no false empty state. They are saved after every refresh.
  - Access loss deletes the repository's snapshot.
  - Retention: configurable (`Cache:RetentionDays`, default 30), only watched repositories, at most `Cache:MaxCachedResponses` responses per account.
  - Sign-out removes the account's snapshots and ETag bodies (`AccountService`). Storage errors are logged and the app continues without the cache.
- **Wake and network recovery** (`PollingConditions`):
  - A 30 s timer detects a sleep gap; `NetworkChange.NetworkAvailabilityChanged` detects the network returning.
  - Either one resets backoff and refreshes everything promptly.
  - Battery comes from `GetSystemPowerStatus` on Windows (otherwise treated as plugged in). Widget visibility comes from the shell.
- **Pause monitoring:** a Settings checkbox and a tray-menu toggle (`MonitoringPaused` setting).
  - While paused, no requests are made, the badge shows "Paused", cached data stays visible, and manual refresh returns at once.
  - Resuming refreshes overdue parts.
- **Stale and revoked data:** during network errors, values stay and are labeled stale or offline. Revoked credentials or lost permission stop protected access: the coordinator replaces the monitor, and access loss withholds content. Results for removed repositories or superseded options are rejected.

**Checks run**
- `dotnet test --solution RepoWatch.slnx`: 325 passed (three consecutive full runs were green after the race fix).
- New contract tests (`ConditionalRequestTests`):
  - 304 served from cache with `If-None-Match`;
  - a changed ETag replaces the entry;
  - errors and GraphQL are not cached;
  - the per-resource budget goes low and recovers after reset;
  - a rate-limited 403 is not confused with a 304.
- Cache tests (`RepositoryCacheTests`):
  - a full domain snapshot round-trips and is restored as Cached;
  - access loss deletes the snapshot;
  - account isolation, and sign-out clearing only that account (through `AccountService`);
  - ETags persist across restarts;
  - retention removes unwatched and old entries;
  - corrupt and mismatched rows are discarded;
  - a version-1 database upgrades and keeps its settings.
- Scheduling tests (`SchedulingTests`, `PollingMonitorTests`):
  - per-part intervals, the slowdown factors and caps, and backoff growth, cap and jitter;
  - sleep-gap, battery and pause detection;
  - pause stops requests and resume restarts them;
  - network return retries at once instead of waiting out the backoff;
  - focus refreshes that repository first;
  - cached data shows before GitHub answers and is saved afterwards;
  - only Actions repeat on the active interval;
  - plus the Stage 06 cases: offline, throttling, out-of-order results and partial failures.
- **Real app:** the Debug build started with a fresh data folder in demo mode; the database migrated to version 2 with no warnings or errors in the log.

**Review of PR #7 (all findings fixed)**
- **Backoff is per part.** Each section keeps its own failure count, so a failing section backs off alone and healthy parts keep their pace. Only errors from the current pass count; a metadata error left over from an earlier pass no longer turns a successful pass into a failure or "Offline".
  - The new test also exposed a second issue: the 10 s batch window pulled backing-off parts into every pass. Parts in backoff are no longer batched early.
- **The ETag cache is bounded.**
  - The in-memory front is an LRU of 200 responses.
  - Pruning runs at start and hourly. It caps responses per account (`Cache:MaxCachedResponses`, default 2000) and drops responses of unwatched repositories.
  - Removing a repository forgets its snapshot and cached responses at once.
  - Retention is configurable (`Cache:RetentionDays`, default 30; validated).
- **Showing the widget catches up.** Becoming visible again, or returning to mains power, pulls stretched due times in to the regular interval.
- **No writes after sign-out.** Cache writes go through a per-sign-in `AccountCache` handle. `ClearAccount` invalidates earlier handles under the same lock as the writes, so a refresh finishing during sign-out writes nothing back.
- **`RateBudget.LowUntil`** (unused, with a misleading summary) was removed.
- New tests:
  - a failing section next to active Actions;
  - catch-up after showing the widget;
  - a stale handle after clear;
  - the bounded memory front;
  - forgetting a removed repository;
  - pruning caps and unwatched responses.
- `dotnet test`: 331 passed (two full runs).

**Remaining limitations**
- **Not run live:** ETag 304s against github.com and restoring the cache after a real restart haven't been run live. That needs a device-flow sign-in. Request counts and idle CPU/network under live polling are not measured yet; Stage 09 asks for them.
- **Sleep detection** relies on a timer gap; macOS and Linux power events come in Stage 12.
- **Notification history** has no table yet; it is added with notifications in Stage 09, along with notification timestamps that stay separate from refresh timestamps.

**Next concrete task**
- Stage 08 remainder (done below).
## Stage 08 — Modern visuals and real transparency: completed

**DPI check (3 Oct 2026):** the maintainer set Windows display scale to 125% and 150% and reported that the widget scales fine. That closes the last open acceptance item below.

Everything is implemented and checked. The last acceptance item, **DPI scaling at 125%/150%**, needed a Windows display-scale change (a system setting this agent doesn't change); the maintainer did it on 3 Oct 2026 and reported the widget scales fine.

### Part 1 — pulled forward (user request, before Stage 06)

The user asked for a UI uplift ahead of order: optional transparency with a slider, motion, a "radiating" state for in-progress work, and a futuristic space-station look. Stage 06 remained the next stage in order. At the time, the rest of Stage 08 was still pending (all done in Part 2 except the DPI check): density, the accent option, a full manual light/dark × material × background matrix with DPI checks, and the high-contrast/remote fallbacks observed on a real machine.

**Implemented**
- A "space station" theme (`App.axaml`). Dark navy gradient surface with a static deterministic star field (`StarField`, no idle animation), HUD corner brackets (`HudCorners`), a cyan frame glow, and neon status colors with a soft halo per status dot. There is a light variant.
- Real materials through `WindowMaterialService`:
  - Auto/Solid/Transparent/Frosted (acrylic)/Mica via Avalonia `TransparencyLevelHint`.
  - The achieved level is read back from `ActualTransparencyLevel`. `Resolve` decides the surface opacity and any fallback message.
  - Fallbacks:
    - High contrast forces solid.
    - Remote sessions force solid for blur materials.
    - If no transparency is achieved, the widget is solid and says so.
    - Frosted that only gets plain transparency is reported.
- Opacity applies only to the background layers (surface + stars). The frame and all content stay at opacity 1, which is asserted in a test.
  - Below 75% surface opacity, content gets a one-layer halo and brighter secondary text (`DockPanel.legible`).
  - The opacity floor (20%) keeps the widget hit-testable: no click-through.
- Settings → Appearance and the onboarding Appearance step have a Material picker and a Background slider (20–100%, disabled for Solid). Settings also has Motion (System/On/Off) and a line describing what was actually achieved (`VisualStateService`).
- Motion:
  - Running work gets the `:active` pseudo-class on `StatusDot`, which animates an expanding pulse ring.
  - Active rows and runs show an indeterminate scan line, and the header shows one while refreshing.
  - Details slide/fade in.
  - All animations stop under `Window.reduce-motion`, which is set from the Motion setting. `System` follows Windows' client-area animation setting (`SPI_GETCLIENTAREAANIMATION`).
  - Animations run only while something is active; nothing animates when idle.
- `MotionPreference` is persisted in `AppearanceSettings` (round-trip covered).
- **Runtime OS changes:** `SystemVisualsWatcher` re-applies visuals when any of these change:
  - the achieved transparency level, the widget being shown, hidden or minimized, or high contrast;
  - on Windows, `WM_SETTINGCHANGE`, `WM_THEMECHANGED`, `WM_DWMCOMPOSITIONCHANGED`, `WM_POWERBROADCAST` or `WM_DISPLAYCHANGE`.
  Bursts are coalesced into one re-apply.
- The transparency hint is reassigned only when the material changes, so slider drags don't rebuild the backdrop.
- If no transparency is granted, the window background is painted with the surface brush, so the fallback is solid edge to edge.
- The widget pauses its animations while hidden or minimized. The low-opacity halo applies to text only, so the pulse and scan animations never re-blur the content.

**Checks run**
- `dotnet build` (0 warnings) and `dotnet test --solution RepoWatch.slnx`: 255 passed.
- New tests:
  - Material resolution: solid, transparent, opacity floor, Auto minimum, unavailable transparency, high contrast, remote session, frosted→plain-transparency.
  - Only running dots are `:active`, and no visible text or ancestor is faded.
  - The pulse animates with motion on and stays at 0 under reduce-motion (headless render ticks).
  - The settings controls change material, opacity and motion; the slider is disabled for Solid.
- Real Windows 11 (26200) launch in demo mode over a bright striped backdrop. The app logged the achieved levels:
  - Frosted at 55% → AcrylicBlur.
  - Transparent at 35% → Transparent.
  - Mica at 60% → Mica.
  - Solid → solid surface.
  - Light Frosted at 60% → AcrylicBlur.
  - `WindowFromPoint` inside the widget returned the widget in every mode (no click-through).
  - Screenshots are in `artifacts/screenshots/ui-space-station/` (not committed).

**Observed limitations**
- Plain Transparent at low opacity over very busy, high-contrast content is still hard to read, even with the halo. Frosted/Auto is the readable choice and is the default.
- Mica is a system backdrop tinted from the wallpaper. It does not show windows behind the widget.
- High-contrast and remote-session fallbacks are unit-tested but were not observed on a real machine. The same goes for re-applying after a runtime OS change, such as toggling Windows "Transparency effects", battery saver or an RDP connect.
- Whether Avalonia updates `ActualTransparencyLevel` when Windows turns transparency effects off is unverified. The watcher re-reads it on the corresponding broadcast messages.

**Measured resource use** (Debug build, Windows 11, demo data with one running item, 15 s samples, CPU as a share of all cores)

| Mode | CPU | GPU 3D | Working set |
| --- | --- | --- | --- |
| Frosted 30% (halo on), visible | 0.34% | 2.3% | 181 MB |
| Frosted 30%, hidden to tray | 0.00% | 0.0% | 181 MB |
| Frosted 85%, visible | 0.12% | 1.1% | 170 MB |
| Frosted 85%, hidden to tray | 0.00% | 0.0% | 171 MB |

### Part 2 — completion

**Implemented**
- **Visual tokens** in `App.axaml`: corner radii (small 6, medium 8, large 14), caption, title and heading sizes, and row/item paddings (comfortable and compact). Colors stay in the theme dictionaries.
- **Accent** (Settings → Appearance → Accent): Station cyan (default), Nebula violet, Ion blue and Plasma magenta.
  - Status colors never change with the accent.
  - The default Station cyan deliberately shares the "running" hue, so the HUD itself reads as active. The three alternatives use hues that differ from every status color.
  - Only presets are honored: a hand-edited value falls back to the default, so Settings always shows the active accent and every accent has a contrast guarantee.
  - `AccentPalette` updates the HUD brushes (brand, frame, glow, hover, selection) and the Fluent accent (sliders, check boxes, focus) at runtime.
  - The light theme uses a darker variant for contrast.
  - The default light accent changed from `#0081A8` to `#006F8E`: the new contrast test showed the old value at 4.2:1, below 4.5:1, on the light surface.
- **Density** (Settings → Appearance → Density): Comfortable or Compact. Compact uses tighter list rows and item paddings and smaller captions and titles, applied as a `compact` class on every window.
- **Default accent matches the XAML:** applying the default accent reproduces `App.axaml` exactly (alphas, the light colors, and `WidgetBorderBrush` with its own alpha); `App.axaml` stays the source of truth.
- **Light secondary text** darkened from `#5E6C84` to `#56637A`. The old value was 4.45:1 on the lighter end of the painted gradient (`#E3ECF6`). The contrast tests now check every stop of the gradient the widget actually paints, instead of the solid surface color.
- **Light-theme opacity floor:** see-through materials keep at least 75% background in the light theme. The matrix showed dark text on a thin light surface becoming unreadable over dark content. The Settings status line explains this when it applies.

**Checks run**
- `dotnet test --solution RepoWatch.slnx`: 339 passed after the review of PR #8. The secondary-text test was confirmed to fail with the old color (4.45:1).
- New tests:
  - every accent preset reaches ≥ 4.5:1 against the dark and light surfaces;
  - secondary text reaches ≥ 4.5:1 on both themes;
  - unknown accents fall back to the default;
  - applying an accent changes the HUD but not the status colors;
  - the Settings Accent and Density controls work;
  - compact rows are shorter, and titles end inside a 320-px widget, measured in window coordinates (the first version compared parent-relative bounds and could not fail);
  - the default accent reproduces `App.axaml`;
  - changing the accent updates an open widget (brand text) and Fluent's `SystemAccentColor`;
  - the light-theme floor.
- **Real-app matrix** (Windows 11 26200, Debug build, demo data, 96 DPI / 100%): light and dark × Solid, Transparent 50% and Frosted 60% × bright, dark and busy backdrops, 18 screenshots. They are in `artifacts/screenshots/stage08/`, with contact sheets `matrix-dark-sheet.png` and `matrix-light-sheet.png` (not committed). Observed:
  - Solid gives a solid surface. Transparent achieved `Transparent`. Frosted achieved `AcrylicBlur` (real blur of the backdrop), in both themes.
  - Dark theme: readable in all nine cells.
  - Light theme: after the 75% floor, readable in all nine cells. Before it, the light-over-dark cells were washed out.
  - Text, icons and status dots stay fully opaque; only the background layers change.
  - Clicks inside the widget hit the widget (`WindowFromPoint`) in 17 of 18 matrix captures. The one exception was Dark/Frosted on the second matrix run; three dedicated reruns of that case hit the widget. It looks transient (another window briefly on top), but it is recorded here rather than dismissed.

**Open acceptance item (blocker)**
- **DPI above 100%:** this machine runs at 96 DPI, and changing the Windows display scale is a system setting I did not change. Layout and saved placement use device-independent units (Stage 03). The maintainer later checked 125% and 150% and reported the widget scales fine.

**Not verified (environment)**
- **High contrast and remote desktop:** both need system changes or another machine. Their solid fallbacks are unit-tested. The "no transparency granted" fallback (surface painted solid) is unit-tested; it was not seen because this machine grants transparency.

## Stage 09 — Desktop behavior and notifications: completed

**Implemented**
- **Windows build target:** the desktop project builds `net10.0` (portable; tests, later macOS/Linux) and `net10.0-windows10.0.19041.0` (the Windows app, with native toasts via the Windows SDK projections). `EnableWindowsTargeting` keeps the macOS/Linux CI build working. Run with `-f net10.0-windows10.0.19041.0`.
- **Start at login:** opt-in, through the per-user Run key (`IStartupRegistration`, `WindowsStartupRegistration`).
  - The setting is the source of truth: it is reconciled at every start, and a moved executable is re-registered.
  - **Start minimized** applies only when started by the OS (`--startup`) and a tray is available.
- **Single instance per user and data folder** (named mutex plus a current-user-only named pipe): a second launch shows the running widget and exits.
- **Show/Hide shortcut Ctrl+Alt+R** (`RegisterHotKey` on the widget's window, which keeps its handle while hidden). It can be turned off. If another app owns the combination, Settings says so and points to the tray icon. Position lock already existed.
- **Notifications** (`NotificationPolicy` in Core, `NotificationService`, `WindowsToastSink`):
  - **Events:**
    - CI failure: a tracked branch head is failing, keyed by commit and the failing runs' attempts, so an old failed run reappearing in a list doesn't count but a failing re-run does.
    - CI recovery: failing, then passing.
    - A direct review request (team requests don't count).
    - A merge of a tracked PR. The "recently merged" PRs are now fetched in the same GraphQL query; in "Mine" scope, only PRs the user authored.
  - **Once only:** each event is recorded once per account in SQLite (`notification_history`, migration 3) with its outcome: shown, baseline, off or quiet. It is never announced twice, also across restarts, and nothing suppressed bursts out later.
  - **Silent baseline:** the first observation of a repository (start, account change, re-added repository) records events without announcing them. Demo data never notifies, and removed repositories stop at once.
  - **Settings:** master switch, one switch per event type, per-repository switch (Manage repositories), quiet hours with whole-hour start and end, "Hide private repository names and titles", a status line reflecting the OS setting, and **Show a test notification**.
  - **Toasts:** plain text only. Clicking one opens the GitHub page through the validated browser adapter; nothing takes focus before the click. Windows Do not disturb and per-app switches apply.
  - Sign-out clears the account's notification history, through the same per-sign-in cache handle, so nothing is written back afterwards.
- **Connection state:** the widget badge shows what happens next: "Offline · retry 14:05" or "Rate limited · until 14:30". "Reconnect required" keeps its single action.
- **Diagnostics export** (Settings → About): a zip in `diagnostics/` with versions, OS, account, connection, pause, visibility and power state, notification availability, counts per attention level, and the rotated logs. Everything passes through `Redactor` (GitHub tokens of every prefix, Bearer headers, token/code fields, device user codes). No settings documents, cached data, repository names, titles or bodies. Logs already roll daily with 7 files kept and 5 MB per day.

**Checks run**
- `dotnet test --solution RepoWatch.slnx`: 367 passed before the review (372 after).
- **New tests:**
  - **Core policy:**
    - the first observation is a silent baseline;
    - a failure is keyed by commit and attempt, so a re-run is a new event;
    - an old failed run in the list isn't a branch failure;
    - recovery needs a failure first;
    - only direct review requests count;
    - only tracked merges count;
    - private details are hidden;
    - each per-type and per-repository switch works.
  - **Service:**
    - no burst at start;
    - announced once, also across a restart;
    - quiet hours skip events without saving them for later;
    - switched-off repositories and types stay silent;
    - hidden private details;
    - a refusing OS and then re-enabled: no burst;
    - removed repositories and demo data are silent;
    - sign-out stops notifications and clears the history.
  - **Redaction** cases.
  - **The diagnostics archive** contains no token and no login or repository names.
  - **Single-instance activation**, with independent data folders.
  - **Startup registration** against a test registry key, never the real Run key: write, detect a moved executable, remove, idempotent.
  - **The Settings controls.**
- **Real app** (Windows build, Debug, signed out, fresh data folder):
  1. With Start at login on, the real Run entry was `"…\RepoWatch.exe" --startup`. A `--startup` launch with Start minimized stayed hidden in the tray.
  2. A second launch exited with code 0, leaving one instance, and the first instance's widget became visible.
  3. Ctrl+Alt+R hid the widget, and pressing it again showed it.
  4. Idle while signed out with the widget visible, over 20 s: 0.01% of all cores' CPU, 174 MB working set, 124 MB private.
  5. Turning Start at login off removed the Run entry at the next start. Nothing was left registered afterwards.
- **A real Windows toast** shown through `WindowsToastSink` from the Windows build. Windows reported availability Enabled; the toast appeared titled "Repo Watch" (screenshot `artifacts/screenshots/stage09/toast.png`, not committed).

**Review of PR #9 (all findings fixed)**
- **No history at restart.** Data restored from the cache is a baseline, not a previous state, so the first live refresh after a restart doesn't announce what happened while Repo Watch was closed. Service test: start from a cached snapshot.
- **Review requests are transitions.** The event is the change from "not requested" to "requested", keyed by the pull request's update time. Pushes don't repeat it, and a re-request after reviewing is a new event.
- **CI failure** announces only failures that weren't already failing on that branch, so fewer failures after a re-run is not news.
- **Merges in "Mine" mode** come from the repository's recently merged list (as in "All"), filtered to tracked pull requests. That also covers ones under review. The service remembers pull requests seen open in the last hour, so a merge that shows a poll late is still caught.
- **SingleInstance:**
  - The pipe name includes the session.
  - The listener waits 500 ms after IOException or UnauthorizedAccessException instead of spinning.
  - The parallel test run exposed a real bug: `ReleaseMutex` throws when `Dispose` runs on another thread. The handle is now just closed (ownership never depends on waiting).
- `dotnet test`: 372 passed (three full runs). The real-app checks 1–5 were repeated with the same results.

**Remaining limitations**
- **Clicking a toast after Repo Watch has exited** does nothing: there is no COM activator. While it runs, clicks open the page. The toast has no app icon yet.
- **Live notifications from real GitHub events** weren't triggered: that would mean causing a CI failure or a review request in a real repository, which is a repository mutation that isn't authorized. The event logic is covered end to end with fixtures.
- **Network use while signed in and polling** was not measured: that needs a device-flow sign-in. Signed-out idle CPU and memory are recorded above.
- **Old history:** notification history older than the retention period (30 days) is pruned. A state that has been failing for longer than that would be announced once more after a restart.
- **Merges** are found among the 10 most recently merged pull requests of the repository.

**Next concrete task**
- Stage 10: the relay. That means an ASP.NET Core webhook receiver with signature verification and durable deliveries, plus authenticated SSE, with the desktop client falling back to polling.

## Stage 10 — Near-real-time delivery (relay): completed

**Implemented**
- **`src/RepoWatch.Relay`** (ASP.NET Core, minimal APIs; [docs/relay.md](relay.md)):
  - **`POST /webhooks/github`:**
    - HMAC-SHA256 verification of `X-Hub-Signature-256` over the raw bytes, with a constant-time compare.
    - The delivery is stored in SQLite **before** the 202 answer.
    - The unique `X-GitHub-Delivery` makes redeliveries answer 200 and process once.
    - Unsubscribed events (e.g. `ping`) are accepted and ignored.
  - **`DeliveryProcessor`:** background processing in order, with exponential-backoff retries up to `MaxDeliveryAttempts`. Malformed payloads fail at once. Retention pruning.
  - **`EventMapper`:** reads only IDs and the action.
    - workflow_run/job → Actions.
    - check_run/suite and status → Actions and pull requests.
    - pull_request and review → pull requests.
    - issues → issues.
    - repository → metadata (deleted → access ends).
    - installation deleted/suspend, installation_repositories removed, github_app_authorization revoked → revocation.
  - **`POST /sessions`:** the user's GitHub token in the Authorization header only. The relay asks GitHub for the user and for the repositories reachable through the user's installations, then grants only the requested repositories that both the user and the app can access. Random session tokens are stored hashed and last 15 minutes. At most 10 sessions per user.
  - **`GET /events`** (SSE):
    - Session-authenticated, with `Last-Event-ID` replay from a bounded buffer.
    - `reset` on a gap, on an ID from another relay run, or on outbox overflow.
    - `revoked` per repository or for the whole session; `expired` at the end of the session.
    - Keep-alives.
    - Sequence IDs start from the start time, so IDs from an earlier run are recognized as gaps.
  - **Config validation:** the relay refuses to start without a webhook secret (at least 16 characters). `appsettings.json` has no secret.
  - **Deployment files:** a `Dockerfile` (non-root, port 8080, data volume) and `.dockerignore`. Nothing was deployed.
- **Desktop:**
  - `RelayClient` (session; SSE reader with `SseParser`) in `RepoWatch.GitHub`.
  - `RelayLink`, attached to the polling monitor when `Relay:BaseUrl` is set:
    - **On connect:** shows Live and reconciles everything.
    - **`invalidate`:** refreshes just that repository's parts. Unknown repositories are ignored.
    - **`revoked`:** refreshes the repository (GitHub decides what access remains); a session-wide revoke or `expired` gets a new session.
    - **`reset`:** reconciles everything.
    - **Session renewal:** a minute before expiry. The link reconnects when the watchlist changes, and backs off up to 2 minutes on failure.
  - **Polling fallback:** polling continues all along. While live it only reconciles (×4 slower). The badge shows **Live** only while the stream is connected and refreshes reach GitHub.
  - The relay `HttpClient` has no request timeout (long-lived stream).
- The Events API is never used.

**Checks run**
- `dotnet test --solution RepoWatch.slnx`: 391 passed (Release build clean); 395 after the review.
- **Relay tests** (16, in-memory server with a fake GitHub API):
  - signatures over exact bytes (unsigned, forged, reformatted body);
  - stored before the 202; a duplicate is processed once;
  - a failed processing attempt is retried;
  - sessions only include repositories GitHub confirms, and alice never receives bob's repository;
  - tokens only in the header: a query-string token gets 401, and no token appears in URLs sent to GitHub;
  - replay of missed events in sequence order;
  - `reset` after a buffer gap and for an unknown ID;
  - installation_repositories removed → per-repository `revoked`, and later events for that repository aren't delivered;
  - github_app_authorization revoked → the session ends and can't be reused;
  - uninstalling ends the session;
  - session expiry (fake clock) sends `expired` and returns 401 afterwards;
  - payload content is ignored.
- **End-to-end tests** (real `PollingRepositoryMonitor` and `RelayLink` against the in-memory relay):
  - a signed webhook refreshes exactly that repository's part, and the badge shows Live;
  - a refused relay keeps polling;
  - events for another user's repository never arrive;
  - an access removal triggers a metadata refresh;
  - when the relay vanishes (the stream ends, then connections are refused), the badge falls back to Polling.
- **Measured latency** from a signed webhook to the targeted refresh request (in-process relay; real HTTP pipeline, no network): 21.9, 22.0, 18.9, 21.4 and 21.5 ms over 5 runs.
- `RelayClient` tests: the SSE parser (ids, multi-line data, comments), token only in the header, refusal and unreachable relay reported.
- **Real relay process** (Kestrel on 127.0.0.1:5088):
  - without a secret it exits with code 1 and a clear message;
  - `/healthz` answers 200;
  - an unsigned webhook gets 401, a signed one 202, and a redelivery 200;
  - `/events` without a session gets 401.

**Review of PR #10 (all findings addressed)**
- **Half-open streams:** the client fails a stream after 60 seconds without any line (three missed keep-alives), so after sleep, a network switch or a NAT timeout the widget leaves Live within a minute instead of about 14 minutes. `RelayLink` also restarts the stream when waking from sleep and when the network returns.
- **Per-repository live coverage:**
  - Only the session's allowed repositories poll at the slower live rate.
  - **Live** shows only when every watched repository is covered; partial coverage shows **Polling**.
  - The relay now fails a session (502) when a GitHub installation listing fails, instead of silently covering less, and answers 403 when nothing is allowed. The link then waits for a watchlist change.
- **Coalescing restart signal:**
  - A bounded channel holding at most one pending signal replaces the semaphore.
  - The stream watcher is always released when a stream ends, so no waiter is left on a dead stream.
  - The subscribed set is recorded before pending signals are cleared. The new test caught a burst causing two sessions before this ordering fix.
- **`Relay:AppId`** is now required. Only installations of Repo Watch's own app count, so a token issued to another app opens no session.
- **Collaborator, team and organization removals:** documented in docs/relay.md, with at most a 15-minute window. The events that would report them need organization *Members* permission, which Repo Watch doesn't request.
- **No full refresh on every connect:** only a first connect or a `reset` reconciles everything. A renewal resumes from `Last-Event-ID`. Rate-limit notes are in docs/relay.md.
- **Startup with a mistyped setting:** the real relay process showed a crash with a stack trace for an empty or non-numeric `Relay:AppId`. It now reports "Configuration error: …" and exits with code 1.
- **New tests:**
  - a stream that goes silent leaves Live;
  - rejected repositories get no events and keep the normal pace;
  - partial coverage shows Polling;
  - one new session per watchlist burst after a dropped stream;
  - a session fails on a GitHub error, is refused when nothing is allowed, and is refused for another app's token.
- `dotnet test`: 395 passed (two full runs); the relay and end-to-end suite passed three runs in a row.
**Remaining limitations**
- **Intermittent test failure:** `SqliteSettingsStoreTests.Concurrent_first_run_initialization_does_not_fail` (8 threads initializing a new database at once, 40 rounds) failed once in 7 full-suite runs and could not be reproduced: 0 failures in 12 isolated runs, 6 parallel stress runs and 8 more full runs. The original output wasn't kept; the assertion now names each exception for the next occurrence. Since Stage 09, the app's single-instance guard prevents concurrent first-run initialization in practice.
- **Not deployed:** real GitHub webhook delivery and real end-to-end latency were not measured. That needs a deployed HTTPS relay and the GitHub App's webhook configured, both maintainer actions that weren't authorized.
- **One instance:** sessions and the replay buffer are in memory; after a restart clients reconcile. Multiple instances would need shared state.
- **No desktop live smoke check against a running relay with a real sign-in:** the end-to-end tests use a fake GitHub for both the relay and the data source.

**Next concrete task**
- Stage 11: a reproducible Windows release build (self-contained publish), About/version with a "Check for updates" link, README setup/use/troubleshooting and a manual validation checklist, and running the release build.

## Stage 11 — Package and validate the Windows release: completed

**Implemented**
- **Release build:** `scripts/publish-windows.ps1`.
  - Locked restore, Release build and all tests, then a self-contained `win-x64` publish of the Windows TFM.
  - Output: `artifacts/release/RepoWatch-<version>-win-x64.zip` plus `.sha256`. The zip is a portable folder (`RepoWatch/RepoWatch.exe`): the runtime is included, there is no installer and no admin rights are needed. No debug symbols are built or shipped (see below).
  - Reproducible:
    - `Deterministic` with CI path mapping;
    - `win-x64` in `RuntimeIdentifiers`, so the committed lock files cover the release graph; both the explicit restore and the publish's own restore run locked;
    - no debug symbols: Avalonia's XAML compiler rewrites `RepoWatch.dll` after Roslyn and records absolute PDB/.axaml paths that path mapping misses, which made two clones at different paths differ;
    - zip entries sorted ordinally and stamped with the commit time.
    - The script warns on uncommitted **or untracked** files and prints the SDK and PowerShell versions. Hashes match across machines only with the same toolchain (`global.json` rolls forward; zip compression comes from PowerShell's runtime).
- **Icon:** `Assets/RepoWatch.ico` (16–256 px, generated by `scripts/make-icon.ps1`) is the executable's `ApplicationIcon` and the window/tray icon. The runtime-drawn icon is gone.
- **About:** version and commit (`0.1.0 (f498e55)`), also in logs and diagnostics. **Check for updates** opens `Updates:ReleasesUrl` (default: this repository's releases page; validated as an https page on the GitHub web host; empty hides it). Nothing is downloaded or run automatically.
- **Docs:** README rewritten for users (install, first run, use, troubleshooting, privacy and security, uninstall, releases); `docs/validation-checklist.md` (manual release checklist); `docs/architecture.md` release section.
- **CI:** a `windows-release` job (manual workflow) runs the publish script and uploads the zip and hash as a workflow artifact. It does not create a GitHub release.
- Stale comments (settings "later stages add more", the runtime icon note) removed. No placeholder controls remain; demo data appears only in labeled demo mode.

**Checks run**
- `dotnet test`: 402 passed (new: releases URL validation, Check for updates opens only the configured page and reports a browser failure, hidden without a URL).
- **Reproducibility:** `publish-windows.ps1` on two fresh clones at different paths (`…/cloneA` and `…/cloneB/nested/path`), commit 1da4856: identical SHA-256 `ed9f31a2…`; 54.5 MB zip, 247 files. Clone A ran the full script including the tests (402 passed). Before 1da4856 the clones differed only in `RepoWatch.dll` (the embedded PDB path).
- **Release exe (extracted zip, Windows 11):**
  - Start at login writes the `Run` value; a `--startup` launch with Start minimized stays hidden; turning it off removes the value.
  - A second launch exits (code 0) and shows the running instance.
  - Ctrl+Alt+R hides and shows the widget.
  - Idle, signed out: 0.09 % CPU, 169 MB working set, 119 MB private over 20 s.
  - Demo launch at 400×520; placement survives a restart; an off-screen window returns to the work area; closing hides to the tray.
  - Signed-out launch shows the sign-in state. Settings → About shows `Repo Watch 0.1.0 (9cdd779)` (pre-commit build), Check for updates and the new title-bar icon. Frosted material was achieved.

**Review of PR #11 (all findings addressed)**
- README status no longer presents notifications from real GitHub events or the relay with real webhooks as live-verified (fixtures only, as Stages 09 and 10 record).
- Publish script:
  - untracked files mark the build dirty;
  - the publish's own restore runs locked (`-p:RestoreLockedMode=true`);
  - zip entries are sorted ordinally;
  - the SDK and PowerShell versions are printed. The cross-machine hash claim is qualified by toolchain.
- The built-in `Updates:ReleasesUrl` no longer fails startup when `GitHub:WebBaseUrl` points elsewhere; *Check for updates* is hidden instead. An explicitly configured URL on another host is still a configuration error.
- **Re-run on the head commit (d74acc7) zip:**
  - Two fresh clones at different paths gave the same SHA-256 `c9204bba…`; toolchain .NET SDK 10.0.401, PowerShell 7.6.6; no dirty warning; 404 tests passed inside the script.
  - Release exe from that zip, all as expected:
    - start at login and minimized start;
    - single instance;
    - Ctrl+Alt+R;
    - idle 0.01 % CPU, 174 MB working set;
    - placement restore, off-screen recovery, close to tray;
    - signed-out state;
    - About shows `Repo Watch 0.1.0 (d74acc7)` with *Check for updates*.
- One unidentified test failure in a working-tree run (1 failed of 404); 11 later full runs passed. It is most likely the known intermittent `Concurrent_first_run_initialization_does_not_fail` (Stage 10 limitations); the output wasn't captured.

**Fresh-user live run on the release build (3 Oct 2026, Windows 11, zip from d74acc7, empty data folder, driven through UI Automation; device code approved by the maintainer on GitHub)**
1. First start: the widget, the tray and the onboarding window. *Sign in with GitHub* showed a device code; after approval it signed in as `github.com/147987379`.
2. Grant access: the existing installation was detected ("already has access through: PantaKoda, All repositories"), so the step could be skipped.
3. Choose repositories: all 97 repositories loaded over several pages, 60+ private. Nothing was preselected ("0 of 97"). Search and checkboxes selected `repo-watch` and `24go` ("2 of 97").
4. Appearance: background set to 70% (the light theme's 75 % readability floor applied).
5. *Open widget*: `Polling`, both repositories with live data. Cross-checked against the REST API:
   - both private (`repo-watch` was made private during this session, and the widget showed the current value);
   - no runs or checks on `main`, so "No checks";
   - 0 open PRs and 0 open issues.
   These repositories had no failed run to show; failed runs were verified live in Stage 06.
6. Restart: no onboarding, still signed in (from Credential Manager), the same two repositories and the saved appearance.
7. Settings → About → *Check for updates* opened the releases page ("You have Repo Watch 0.1.0 (d74acc7)…").
8. *Sign out*:
   - the widget and settings returned to "Not signed in";
   - the Credential Manager entry was removed;
   - `repository_snapshots` and `http_cache` were empty;
   - the account's watchlist (2 repositories) was kept for the next sign-in;
   - no token-like values were in the database.
   Test data and the extracted release were deleted afterwards. No `Run` value remained.

No code was edited and no tokens were copied by hand at any point.

**Remaining limitations**
- **Not code-signed:** SmartScreen warns.
- **No GitHub release published.**
- **The GitHub App is installable only on its owner's account.**
- **`PantaKoda/repo-watch` is private:** the default *Check for updates* page is visible only to its owner until the repository is public or `Updates:ReleasesUrl` points elsewhere.
- These four are maintainer decisions.
- **No debug symbols in releases** (stack traces without line numbers).
- **x64 only;** ARM64 emulation not verified.
- **Stage 08 DPI check is still open.**
- **Not verified live:** notifications from real GitHub events and the relay with real webhooks (fixtures only).

**Next concrete task**
- UI changes as requested (Stage 08's display-scale check has since passed). Stage 12 (macOS/Linux) is a later release.

## After release — widget list usability (feature branch `widget-ux-filters`)

Requested by the maintainer after using 0.1.0:

**Implemented**
- **Sort by recent activity:** a third ordering, *Recent activity*, next to *Needs attention* and *My order*, in the widget and in Settings.
  - Last activity is the newest of GitHub's `pushed_at` (any branch, now read from `GET /repositories/{id}`), the latest workflow run, pull request and issue updates.
  - Unknown activity sorts last; ties keep the manual order.
  - Each row shows it (e.g. "3h ago").
- **Filter bar** above the list:
  - a name filter (Ctrl+F; Esc clears it; Down moves into the list);
  - a sort menu, saved per account; in demo mode it applies for the session only;
  - *Hide idle repositories*, remembered in the app settings (`Window.HideIdleRepositories`).
  - **Idle** means: no failure or problem, nothing queued or running, no open pull requests in the watched scope and no open issues. A section that is turned off counts as idle; a repository whose data hasn't loaded is never hidden.
  - The footer says "N of M shown".
  - When the filters hide everything, the list says why and offers *Show all repositories*.
  - Filters change only what is shown, never what is monitored.
- **Visibility tag:** a small *Private* or *Public* pill on each row (accent outline for private). It isn't shown before metadata loads.
- **Calmer header:** the moving line under the header now shows only for a refresh you start (Refresh/F5) and for the first load. Before, it ran during every background poll, which with several repositories was almost always. The Refresh button is no longer disabled during background polls. Rows with a workflow actually running keep their own line.
- **Narrow widths:** the connection pill trims instead of sliding under the header buttons. This predated the branch and is visible at 320 px.
- Placeholder text in the filter is dimmed by color at full opacity, keeping the "text is never faded" rule. A UI test caught the theme's 50% placeholder opacity.

**Checks run**
- `dotnet test`: 422 passed. New tests:
  - activity ordering and last-activity selection;
  - idle rules (open PR, open issue, running, failing, not loaded, sections turned off);
  - `pushed_at` parsing;
  - widget name filter and Esc;
  - hide idle with persistence and the all-hidden state;
  - sort saved per account and session-only in demo;
  - Private/Public tags;
  - header activity for background polls vs. manual refresh vs. first load.
- Real window in demo mode at 400 px and 320 px, driven through UI Automation:
  - the filter bar, tags and activity times render;
  - hiding idle removes `dotfiles` ("4 of 5 shown");
  - *Recent activity* orders api-service (1m), web-app (7m), legacy-tool (3h), dotfiles (12d), then the inaccessible repository.

**Limitations**
- Activity uses data Repo Watch already loads; there is no extra request per repository. Repositories cached before this version show no push time until their metadata refreshes.

**Review of PR #13 (all findings addressed)**
- **Rows no longer vanish during interaction:** with *Hide idle* on, a repository that becomes idle in a background refresh stays while the pointer or keyboard focus is in the list. It is removed when the interaction ends, like a deferred reorder. Filter changes you make still apply at once.
- **A failed refresh is not idle:** a section with a failed latest refresh (stale cached value) keeps the repository visible, together with its freshness warning.
- **Footer counts:** failing and needs-attention counts include repositories hidden by the filters.
- **First load is latched:** it ends once any repository's metadata has an outcome (data, error or lost access) or a refresh pass finishes. Repositories that never get metadata no longer bring the header line back on every poll.
- Stage 08 wording updated.
- **The intermittent database test is fixed.** `Concurrent_first_run_initialization_does_not_fail` (Stage 10 limitations) was reproduced in a stress loop: 1 failure in about 20–45 runs. The error was SQLite error 1 ("SQL logic error") from `BEGIN IMMEDIATE` in `LocalDatabase.Initialize` while another connection switched the file to WAL; the busy timeout doesn't retry that. Initialization of one database file is now serialized across threads and processes by a named mutex (hash of the path). After the fix: 0 failures in 100 stress runs, and the full suite (427) passed twice. The test now reports the failing `LocalDatabase` frame.

**Next concrete task**
- Rebuild the installed app from main after merge. Further UI changes as requested. Stage 12 (macOS/Linux) is a later release.

## After release — open a repository from the list (feature branch `row-open-in-browser`)

**Implemented**
- Each row in the main list has a small open-in-browser button that opens the repository's GitHub page. Before, you had to open the details view first.
  - **Discreet by colour, never faded:** the dim secondary colour at rest (the same contrast as secondary text) and the accent colour on the hovered or selected row.
  - **Only with a known page:** it appears only when the repository's page is known. An inaccessible repository gets none.
  - **Separate from the row:** clicking the button doesn't also open the details; the row's tap handler ignores clicks inside buttons.
  - **Keyboard:** Ctrl+Enter on the selected row does the same; on a repository without a known page it says so in the footer. Enter and Space still open details.
  - **Not focusable:** clicking the button selects its row, so Enter, Space and Ctrl+Enter always act on the row you clicked.
  - **Accessible name:** "Open owner/name on GitHub".
- The link goes through the same validated browser adapter (GitHub hosts only), and failures are reported in the widget footer.

**Checks run**
- `dotnet test`: 429 passed. Two new headless UI tests:
  - a real click on a row's button opens that repository and not the details; only rows with a URL have a button;
  - Ctrl+Enter opens the selected repository.
- Real window in demo mode: the icon shows on the four repositories with a page and not on the inaccessible one.

**Review of PR #14 (all findings addressed)**
- **No fading:** the resting icon was 40% opacity on the dim colour (about 1.8:1 in Light), against the "icons stay readable and fully opaque" rule. It is now fully opaque and quiet by colour only. A UI test checks no row button or descendant is faded.
- **Keyboard and the clicked row:** the button was focusable, so after clicking row B's button with row A selected, Enter opened A. The button is now not focusable and clicking it selects its row. A UI test clicks B's button, presses Enter and gets B's details.
- **Ctrl+Enter without a page** shows "This repository's GitHub page isn't available right now." (UI test).
- **Shift+Enter and Shift+Space** open details again, as before this PR. The test comment was corrected.
- `dotnet test`: 432 passed.

**Next concrete task**
- Further UI changes as requested. Rebuild the installed app after merge.

