# Repo Watch

A Windows-first desktop widget for monitoring GitHub Actions, pull requests and issues, built with C#, .NET and Avalonia. The shared core and UI are kept portable for later macOS/Linux releases.

> **Status: early development.** The widget, tray icon, settings window and window placement work, with labeled demo data. GitHub sign-in works and was verified against github.com: device flow, Windows Credential Manager storage, token renewal and sign-out (see [docs/github-app-setup.md](docs/github-app-setup.md)). Repository selection and live data come in Stages 05–06. GitHub sign-in, repository monitoring and the widget UI are **not implemented yet**. See [docs/PROGRESS.md](docs/PROGRESS.md) for current state.

## Platform baseline

| Item | Version |
| --- | --- |
| .NET SDK | 10.0 (LTS), pinned by `global.json` (`10.0.100`, rolls forward to the latest installed 10.0 feature band) |
| Target framework | `net10.0` |
| Avalonia | 12.1.3 |
| Supported Windows | Windows 10 version 1809 or later and Windows 11, x64 and ARM64. Mica backdrop (later stage) requires Windows 11. |
| macOS / Linux | Shared code builds in CI; **not** release-ready or supported yet. |

Package versions are pinned centrally in `Directory.Packages.props`, and NuGet lock files (`packages.lock.json`) are committed for reproducible restores.

## Build, run and test

Prerequisite: the .NET 10 SDK.

```bash
dotnet restore RepoWatch.slnx
```

```bash
dotnet build RepoWatch.slnx
```

```bash
dotnet run --project src/RepoWatch.Desktop
```

To explore the widget with labeled sample data (no GitHub access needed):

```bash
dotnet run --project src/RepoWatch.Desktop -- --demo
```

```bash
dotnet test --solution RepoWatch.slnx
```

Tests use xunit v3 on Microsoft.Testing.Platform (opted in via `global.json`), so `dotnet test` takes `--solution` / `--project` options rather than a positional path. Headless UI tests write screenshots to `artifacts/screenshots/` (not committed).

## Using the widget

- **Move:** drag the header (unless *Lock position* is on). **Resize:** drag the bottom-right grip. The position and size are remembered per monitor setup. If the widget would open off-screen, it is moved back onto the primary display.
- **Tray:** on Windows the widget lives in the notification area. Click the tray icon to show or hide it; its menu has *Show widget*, *Settings…* and *Quit*. Closing or hiding the widget keeps Repo Watch running. Where no tray is available, the widget stays in the taskbar and closing it exits.
- **Keyboard:** <kbd>Tab</kbd> to the list, arrow keys to move, <kbd>Enter</kbd> to open details, <kbd>Esc</kbd> to go back or collapse, <kbd>F5</kbd> to refresh, <kbd>Ctrl</kbd>+<kbd>,</kbd> for settings.
- **Demo mode:** started with `--demo` or *Explore demo data*. A banner stays visible while sample data is shown, and links open GitHub documentation because the sample repositories don't exist.

### Which checks need what

| Check | Requirement |
| --- | --- |
| Restore, build, unit tests | Any OS with the .NET 10 SDK |
| Running the desktop app | A desktop session; Windows is the delivery target |
| Tray, window materials, Credential Manager, startup registration (later stages) | Windows |
| Sign-in and live GitHub smoke tests (Stage 04+) | A registered GitHub App client ID and a GitHub account |
| Live mode (Stage 10) | A deployed relay with the GitHub App's webhook secret |

## Configuration

Configuration holds only **public** deployment values. Secrets never belong here. Sources, in increasing precedence:

1. Built-in defaults.
2. `appsettings.json` next to the executable.
3. A per-user override file: `%LOCALAPPDATA%\RepoWatch\repowatch.config.json`.
4. Environment variables prefixed `REPOWATCH__`, with `__` as the section separator, e.g. `REPOWATCH__GitHub__ClientId`.

| Key | Default | Meaning |
| --- | --- | --- |
| `GitHub:ClientId` | empty | Public client ID of the Repo Watch GitHub App. Empty means sign-in is unavailable. |
| `GitHub:AppSlug` | empty | The app's URL slug, used for the "Grant repository access" link. |
| `GitHub:WebBaseUrl` | `https://github.com` | GitHub web base URL (https only). |
| `GitHub:ApiBaseUrl` | `https://api.github.com` | GitHub REST base URL (https only). |
| `Polling:ActiveWorkflowSeconds` | 20 | Target refresh for running workflows (5–3600). |
| `Polling:PullRequestSeconds` | 90 | Target PR refresh (5–3600). |
| `Polling:IssueSeconds` | 120 | Target issue refresh (5–3600). |
| `Polling:QuietSeconds` | 180 | Target refresh for idle repositories (5–3600). |
| `Relay:BaseUrl` | empty | Optional live-update relay. https, or `http://localhost` for development. |

Unknown keys, wrongly typed values, malformed JSON and invalid values stop startup with a window listing each problem, the setting to change and the files that were read. Problems are also logged.

`REPOWATCH_DATA_DIR` (single underscore; not a configuration key) relocates the per-user data folder, which is useful for portable use and isolated testing.

### Local data

| Path (default) | Contents |
| --- | --- |
| `%LOCALAPPDATA%\RepoWatch\repowatch.config.json` | Optional configuration override |
| Windows Credential Manager, `RepoWatch:github/<host>/<userId>` | GitHub access and refresh tokens. The only place tokens are stored. Removed on sign-out. |
| `%LOCALAPPDATA%\RepoWatch\repowatch.db` | SQLite database: settings now, cached snapshots later. Never contains tokens. |
| `%LOCALAPPDATA%\RepoWatch\logs\` | Daily rolling logs, newest 7 files kept. Tokens and private content must never be logged. |

## Repository layout

```
src/RepoWatch.Core      Domain models, policies, configuration contracts (no UI/OS dependencies)
src/RepoWatch.GitHub    GitHub auth/API clients, synchronization, relay client
src/RepoWatch.Desktop   Avalonia app, storage, settings, platform adapters
tests/                  Focused tests
docs/                   Architecture and progress
```

See [docs/architecture.md](docs/architecture.md).
