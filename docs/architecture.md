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

## Configuration vs. user settings vs. secrets

| Kind | Example | Stored in |
| --- | --- | --- |
| Deployment configuration (public) | GitHub App client ID, API base URL, polling targets | `appsettings.json`, user override file, environment |
| User settings (Stage 02+) | Watchlist, order, appearance, notifications, window placement | Local SQLite, schema-versioned, isolated by host + account |
| Secrets (Stage 04+) | Access/refresh tokens | OS credential store only (Windows Credential Manager first); session-only fallback if unavailable |

No client secret or private key is ever embedded in the desktop app. Server-only GitHub App credentials belong to the relay's secret configuration.

## Testing approach

- `RepoWatch.Core.Tests`: pure policy and validation logic, plus GitHub endpoint composition.
- `RepoWatch.Desktop.Tests`: configuration loading against real temporary files, with injected environment variables.
- Later stages add HTTP-contract fixtures (GitHub), persistence round trips (SQLite) and headless UI checks (`Avalonia.Headless.XUnit`, version-pinned).
