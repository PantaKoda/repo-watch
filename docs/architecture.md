# Architecture

This describes what exists now and the boundaries later stages must respect. `AGENTS.md` is the implementation contract.

## Projects and dependency direction

```
RepoWatch.Desktop ──► RepoWatch.GitHub ──► RepoWatch.Core
        └──────────────────────────────────►┘
RepoWatch.Relay (Stage 10) ──► RepoWatch.Core
```

| Project | Owns | Must not depend on |
| --- | --- | --- |
| `RepoWatch.Core` | Domain models, status aggregation, attention/notification policy, service contracts, configuration options and validation | Avalonia, OS-specific APIs, HTTP |
| `RepoWatch.GitHub` | Device-flow auth, REST/GraphQL clients, pagination, conditional requests, synchronization, relay client | Avalonia, OS-specific APIs |
| `RepoWatch.Desktop` | Composition root, Avalonia views and view models, SQLite storage, settings, platform adapters | — |
| `RepoWatch.Relay` | Webhook receiver, durable deliveries, authenticated SSE | Desktop |

Platform adapters (credentials, browser launch, tray, notifications, startup registration, shortcuts, window materials) are interfaces whose contracts live in Core, with implementations in clearly separated `Desktop/Platform/<OS>` namespaces. Shared projects make no unconditional Windows-only calls.

## Startup and composition

`Program.Main` in `RepoWatch.Desktop`:

1. Resolves `AppPaths`: the per-user data folder, or `REPOWATCH_DATA_DIR` when set.
2. Creates logging: debug output plus a daily rolling file under `logs/`.
3. Loads configuration with `ConfigurationLoader`: shipped JSON, then the user override, then `REPOWATCH__*` environment variables. Binding is strict, so unknown keys or wrong types are errors. `RepoWatchOptionsValidator` (Core) then checks URLs, the client ID shape and interval bounds. Errors are returned as data rather than thrown.
4. Builds the DI container (`CompositionRoot`) and starts Avalonia with `App`.
5. `App` shows the main window, or `ConfigurationErrorWindow` when configuration is invalid. That window lists each error and its fix, and offers Open folder and Quit.

UI code observes view-model state and issues commands. Polling loops, HTTP and persistence live in services, never in views.

## Shell and UI (Desktop)

```
AppShell ── owns ──► WidgetWindow ◄─ binds ─ WidgetViewModel ─ observes ─► MonitorHost.Current : IRepositoryMonitor
   │                  SettingsWindow ◄─ binds ─ SettingsViewModel                 ├─ SignedOutMonitor (no account)
   ├─ TrayService (Show / Settings / Quit)                                        ├─ DemoRepositoryMonitor (explicit, labeled)
   ├─ WindowPlacementService ─► SettingsService ─► ISettingsStore (SQLite)        └─ PollingRepositoryMonitor (signed in, watchlist non-empty)
   └─ IShell (hide/show/settings/quit for view models)
```

- View models observe `IRepositoryMonitor.Changed` (any thread), marshal to the UI thread through `IUiDispatcher`, and call `RefreshAsync`. They never poll or perform HTTP.
- `CollectionReconciler` updates bound collections in place. While the user is interacting with a list, reordering is deferred so a focused or hovered row never moves.
- Platform adapters live in `Desktop/Platform/*`: tray, window placement, browser launch (`IExternalBrowser` + `ExternalLinkPolicy`), UI dispatcher and icon.
- Window placement uses `Core/Layout/PlacementPolicy` (pure geometry): the display key, the reachability check and the default top-right position.

## Authentication

```
AccountViewModel ─► AccountService ─┬─► DeviceFlowClient / DeviceFlowSignIn  (POST github.com/login/device/code, /login/oauth/access_token)
                                    ├─► GitHubUserClient                       (GET api.github.com/user → AccountKey = host + user ID)
                                    ├─► AccountSession (per account)           (renew 5 min before expiry, single-flight, persist rotation first)
                                    ├─► ICredentialStore                       (WindowsCredentialStore | SessionCredentialStore)
                                    └─► SettingsService / MonitorHost          (active account, last login; NotSignedIn/Polling/Offline/ReconnectRequired)
```

- Only the public client ID is used. No client secret or private key exists in the desktop app.
- API clients get tokens from `AccountSession.GetAccessTokenAsync` and call `HandleUnauthorizedAsync` on 401. They observe `Lifetime`, which is cancelled on sign-out or reconnect, so results from a closed session are discarded.
- A rejected renewal deletes the dead tokens and sets `ReconnectRequired`. Nothing retries until the user signs in again.

## Repository data (GitHub + Desktop)

```
MonitorCoordinator ─► IRepositoryMonitorFactory (GitHubMonitorFactory) ─► PollingRepositoryMonitor ─► RepositoryRefresh ─► IRepositoryDataSource
                                                                          (one loop, one repo at a time)                      └─ RepositoryDataClient
                                                                                                                                  ├─ REST  GET /repositories/{id}                     metadata (follows renames)
                                                                                                                                  ├─ REST  GET /actions/runs, /commits/{branch}/status, /actions/runs?head_sha=
                                                                                                                                  ├─ GraphQL pullRequests + reviews + reviewRequests + mergeable + statusCheckRollup
                                                                                                                                  └─ GraphQL issues (totalCount excludes pull requests)
```

- The coordinator creates a polling monitor only when an account is signed in and its watchlist is non-empty. Watchlist edits apply in place; removals, sign-out and account changes dispose the monitor, which also runs on the session's `Lifetime`.
- `RepositoryRefresh` loads metadata first: 404/403/SSO there means access was lost, and every section's cached content is withheld. Otherwise Actions, pull requests and issues load and fail independently, each keeping its last good value.
- Branch health uses the branch head from the combined-status endpoint (Commit statuses: read), so Repo Watch needs no Contents permission. A head commit with no runs is "No checks", even if older commits failed.
- Merge state comes from GraphQL `mergeable` (conflicts only). Branch protection and required checks are not read, so nothing is shown as "ready to merge". Team review requests are never treated as "for me".
- Intervals are targets (active 20 s, normal 90 s, after failure 60 s). A rate limit pauses all polling until the reset. Stage 07 adds ETags, persistence and the bounded scheduler.

## Domain model (Core)

- **Keys:** `AccountKey` (host + user ID) and `RepositoryKey` (account + repository ID). Logins, owners and names are display metadata and may change.
- **Outcomes:** `CheckOutcome` per run/job/check/status; `CheckRollup` per commit. A passing rollup is not a statement about mergeability, connectivity or outstanding work.
- **Current-commit selection:** `WorkflowRunSelection` (latest attempt per run, newest run per workflow+event, current SHA only) and `CommitChecks` (latest check per app+name, latest status per context).
- **Resource state:** each section of a `RepositorySnapshot` is a `Resource<T>` with its own value, freshness and error. A failure in one section never modifies another. Access loss withholds cached content in every section.

## Persistence (Desktop)

`LocalDatabase` (`repowatch.db` in the data folder) applies ordered SQL migrations tracked by `PRAGMA user_version` and refuses a database from a newer version. Settings are stored as versioned JSON documents per scope (`app`, `account:<host>/<userId>`), encoded by `SettingsCodec`:

1. Read `schemaVersion`. If it is newer than supported, return defaults and never overwrite the stored document.
2. Apply JSON migrations `v → v+1` up to the current version.
3. Deserialize. An individual invalid value (wrong type, unknown enum name, missing required field, invalid account key) is removed by its JSON path and falls back to its default; the rest of the document is kept (`Repaired`).
4. Normalize out-of-range values.
5. Unreadable documents are copied to `settings_backup` before defaults can replace them; repaired documents are backed up and kept until the next save.

Schema change rules:
- **Adding a field with a default** needs no version bump. Unknown properties are preserved via `[JsonExtensionData]` on every settings record, so an older version saving the document does not erase a newer version's fields.
- **Adding an enum member, changing a field's type, or renaming, moving or reinterpreting a field** needs a migration and a version bump. An older version then sees a newer schema and refuses to overwrite the document.

Database initialization takes the write lock (`BEGIN IMMEDIATE`) and re-reads `user_version` inside each migration transaction, so concurrent first runs are safe. `journal_mode = WAL` is set only after the version check, so a database from a newer version is never modified.

`AccountKey` accepts only a bare host (optionally with port), so the same account cannot get two storage keys. Use `AccountKey.ForWebBase` to derive one from a URL.

## Configuration vs. user settings vs. secrets

| Kind | Example | Stored in |
| --- | --- | --- |
| Deployment configuration (public) | GitHub App client ID, API base URL, polling targets | `appsettings.json`, user override file, environment |
| User settings | Watchlist, order, appearance, notifications, window placement | Local SQLite (`settings` table), schema-versioned JSON, isolated by host + account |
| Secrets (Stage 04+) | Access/refresh tokens | OS credential store only (Windows Credential Manager first); session-only fallback if unavailable |

No client secret or private key is ever embedded in the desktop app. Server-only GitHub App credentials belong to the relay's secret configuration.

## Testing approach

- `RepoWatch.Core.Tests`: pure policy and validation logic, plus GitHub endpoint composition.
- `RepoWatch.Desktop.Tests`: configuration loading against real temporary files, with injected environment variables.
- Later stages add HTTP-contract fixtures (GitHub), persistence round trips (SQLite) and headless UI checks (`Avalonia.Headless.XUnit`, version-pinned).
