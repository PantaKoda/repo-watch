# Progress

Status values: `pending`, `in_progress`, `completed`, `blocked`. At most one stage is `in_progress`.

| Stage | Title | Status |
| --- | --- | --- |
| 01 | Project and development baseline | completed |
| 02 | Repository state and settings model | completed |
| 03 | Functional desktop shell | completed |
| 04 | Sign in with GitHub | completed |
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