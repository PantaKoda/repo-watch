# Repo Watch

A Windows-first desktop widget for monitoring GitHub Actions, pull requests and issues, built with C#, .NET and Avalonia. The shared core and UI are kept portable for later macOS/Linux releases.

> **Status: early development (Stage 01 of 12).** The project builds, loads and validates configuration, and opens a baseline window. GitHub sign-in, repository monitoring and the widget UI are **not implemented yet**. See [docs/PROGRESS.md](docs/PROGRESS.md) for current state.

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

```bash
dotnet test --solution RepoWatch.slnx
```

Tests use xunit v3 on Microsoft.Testing.Platform (opted in via `global.json`), so `dotnet test` takes `--solution` / `--project` options rather than a positional path.

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
