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
