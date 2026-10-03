# Repo Watch

A Windows-first desktop widget for monitoring GitHub Actions, pull requests and issues, built with C#, .NET and Avalonia. The shared core and UI are kept portable for later macOS/Linux releases.

> **Status: Windows release 0.2.0.** Sign-in, the repository picker, live GitHub data with caching and desktop integration (tray, startup, single instance, shortcut) were checked on Windows 11 against github.com. Notifications from real GitHub events and the webhook relay with real GitHub webhooks have been verified only with test fixtures so far. macOS and Linux are not supported yet. Open limitations are listed in [docs/PROGRESS.md](docs/PROGRESS.md).

## Install and run (Windows)

1. Download `RepoWatch-<version>-win-x64.zip` and its `.sha256` from the [releases page](https://github.com/PantaKoda/repo-watch/releases), or build them yourself (see [Releases](#releases)).
2. Verify the download: the hash printed by `Get-FileHash RepoWatch-<version>-win-x64.zip -Algorithm SHA256` must match the `.sha256` file.
3. Extract the zip to a folder you can write to (for example `%LOCALAPPDATA%\Programs\RepoWatch`) and start `RepoWatch\RepoWatch.exe`.

The zip is **portable and self-contained**: the .NET runtime is included, nothing is installed and no administrator rights are needed. Later versions can be installed from inside the app (see *Updates* below). The executable is **not code-signed** yet, so Windows SmartScreen may say "Windows protected your PC"; choose *More info → Run anyway* only when the SHA-256 matches. Settings, the watchlist and the sign-in live outside the folder (see [Local data](#local-data)), so updating keeps them. To remove Repo Watch, see [Uninstall](#uninstall).

## First run

The onboarding window walks through **Sign in → Grant repository access → Choose repositories → Appearance → Open widget**:

1. **Sign in with GitHub.** Repo Watch shows a short code. Use *Copy code* and *Open GitHub*, enter the code on github.com and approve. Repo Watch never asks for your password or a personal access token.
2. **Grant repository access.** GitHub decides which repositories the Repo Watch GitHub App may read. *Grant access on GitHub* opens the installation page, where you pick accounts, organizations and repositories. Organizations may need an owner's approval. Skip this step if the app is already installed.
3. **Choose repositories.** Access alone adds nothing to the widget. Tick the repositories to watch; search and owner filters help. Change the list later with Settings → *Manage repositories…*.
4. **Appearance.** Theme, material (Auto, Solid, Transparent, Frosted, Mica) and background opacity. Text stays fully opaque.
5. **Open the widget.** It lives in the notification area and remembers its position.

## Using the widget

- **Move:** drag the header (unless *Lock the widget's position* is on). **Resize:** drag the bottom-right grip. The position and size are remembered per monitor setup. If the widget would open off-screen, it is moved back onto the primary display.
- **Tray:** click the tray icon to show or hide the widget. Its menu has *Show widget*, *Pause monitoring*, *Settings…* and *Quit Repo Watch*. Closing or hiding the widget keeps Repo Watch running. Where no tray is available, the widget stays in the taskbar and closing it exits.
- **Shortcut:** <kbd>Ctrl</kbd>+<kbd>Alt</kbd>+<kbd>R</kbd> shows or hides the widget from anywhere. It can be turned off in Settings, which also says when another app already uses it.
- **Keyboard:** <kbd>Tab</kbd> to the list, arrow keys to move, <kbd>Enter</kbd> to open details, <kbd>Ctrl</kbd>+<kbd>Enter</kbd> to open the repository on GitHub, <kbd>Esc</kbd> to go back, clear the filter or collapse, <kbd>F5</kbd> to refresh, <kbd>Ctrl</kbd>+<kbd>F</kbd> to filter, <kbd>Ctrl</kbd>+<kbd>,</kbd> for settings.
- **Find what matters:** the bar above the list filters by name (<kbd>Ctrl</kbd>+<kbd>F</kbd>, <kbd>Esc</kbd> clears), sorts by *Needs attention*, *Recent activity* or *My order*, and the filter button hides idle repositories (nothing open, running or failing). Each row shows a *Private*/*Public* tag, when it last had activity, and a small open-in-browser button (or press <kbd>Ctrl</kbd>+<kbd>Enter</kbd> on the selected row) that opens the repository on GitHub.
- **Status:** Actions, pull requests and issues are shown separately per repository, each with its own freshness. The header shows `Live`, `Polling`, `Offline`, `Paused` or `Reconnect required`. Its moving line appears only while you wait for a refresh you started or the first load; routine background checks are silent. When a refresh fails, the last data stays visible and is labeled stale.
- **Acting on things:** merges, reviews, comments and workflow re-runs open GitHub in your browser. Repo Watch only reads.
- **Notifications:** CI failing or passing again on a tracked branch, a new review request and a merged pull request you track, each once. Quiet hours, per-repository switches and *Hide private repository names and titles in notifications* are in Settings.
- **Demo mode:** started with `--demo` or *Explore demo data*. A banner stays visible while sample data is shown, and links open GitHub documentation because the sample repositories don't exist.
- **Updates:** Repo Watch checks GitHub for a newer release once a day. When there is one, an **UPDATE** button appears in the header: it shows what changed since your version and offers **Install update**, which downloads the release, checks it against its published checksum, swaps in the new version and restarts (settings and sign-in are kept; the previous version stays next to the app folder). *Check for updates* in Settings → About checks right away and says when there is no newer release. Nothing is ever downloaded unless you click Install. Details: [docs/updates.md](docs/updates.md).
- **About:** Settings → About shows the version and commit.

## Troubleshooting

| Symptom | What to do |
| --- | --- |
| The widget is gone | Click the tray icon (look in the hidden-icons overflow), press <kbd>Ctrl</kbd>+<kbd>Alt</kbd>+<kbd>R</kbd>, or start `RepoWatch.exe` again: a second start shows the running instance. |
| Sign-in fails at once ("Device flow is not enabled", invalid client) | The configured GitHub App is missing device flow, or `GitHub:ClientId` is wrong. See [docs/github-app-setup.md](docs/github-app-setup.md). |
| *Grant access on GitHub* shows a 404, or you can't install the app | The GitHub App can be installed only on its owner's account until the maintainer sets *Where can this GitHub App be installed?* to **Any account**. |
| A repository is missing from the picker | GitHub has not granted the app access to it. Use *Manage access*, then *Refresh list*. Organization repositories may be waiting for an owner's approval or need *Authorize single sign-on*. |
| `Reconnect required` | The session expired or was revoked on GitHub. Choose *Sign in again*; Repo Watch does not retry forever. |
| One section says it is unavailable (e.g. Actions disabled) | That part is reported per repository; the other sections keep working. Check the repository's settings on GitHub. |
| Data is stale or `Offline` | The last data stays, labeled, and refreshes after reconnect or wake. GitHub rate limits slow refreshes down instead of failing them. |
| No notifications | Check Settings → Notifications (*Show a test notification*), quiet hours, and Windows Settings → System → Notifications → Repo Watch. |
| Transparency looks solid | Windows transparency effects are off, high contrast is on, the session is remote or battery saver is on. Repo Watch then uses a solid surface on purpose. Settings shows the achieved material. |
| A configuration error window at start | It names each setting, the problem and the files read. Fix or remove that value. |
| Anything else | Settings → About → *Export diagnostics* creates a redacted archive (no tokens, codes, repository names or content) to attach to an issue. Logs are in `%LOCALAPPDATA%\RepoWatch\logs`. |

## Privacy and security

- **Read-only.** The GitHub App requests read permissions only: Metadata, Actions, Checks, Commit statuses, Issues and Pull requests. Repo Watch connects only to GitHub and, when configured, your relay. There is no telemetry.
- **Tokens** are stored only in Windows Credential Manager (`RepoWatch:github/<host>/<userId>`), never in files, the database, logs, URLs or the clipboard. Without a working credential store, the sign-in lasts for the session only.
- **No secrets ship with the app.** The client ID in `appsettings.json` is public. The desktop app has no client secret and no private key.
- **Sign out** stops monitoring, removes the tokens and deletes that account's cached repository data. Watchlist choices stay for the next sign-in. Local sign-out does not revoke the app on GitHub: use *Review access on GitHub* (Settings → Account) to revoke it there.
- **Repository content is untrusted.** Titles are shown as plain text, and only HTTPS links on the GitHub web host are opened.

## Uninstall

1. In Settings, turn off *Start Repo Watch when I sign in* (removes the `Run` registry value) and choose *Sign out* (removes the tokens and the account's cached data).
2. Quit Repo Watch from the tray menu and delete its folder.
3. Optionally delete `%LOCALAPPDATA%\RepoWatch` (settings, cache, logs) and the registry key `HKCU\Software\Classes\AppUserModelId\RepoWatch.Desktop` (notification identity).
4. Optionally revoke the app on GitHub (Settings → Applications → Authorized GitHub Apps) and uninstall it where it was installed.

## Platform baseline

| Item | Version |
| --- | --- |
| .NET SDK | 10.0 (LTS), pinned by `global.json` (`10.0.100`, rolls forward to the latest installed 10.0 feature band) |
| Target framework | `net10.0` (shared projects, tests); the desktop app also builds `net10.0-windows10.0.19041.0`, the Windows build with native notifications |
| Avalonia | 12.1.3 |
| Supported Windows | Windows 10 version 1809 or later and Windows 11. The release zip is **x64**; ARM64 devices would run it under emulation, which is not verified. Mica requires Windows 11. |
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
dotnet run --project src/RepoWatch.Desktop -f net10.0-windows10.0.19041.0
```

To explore the widget with labeled sample data (no GitHub access needed):

```bash
dotnet run --project src/RepoWatch.Desktop -f net10.0-windows10.0.19041.0 -- --demo
```

```bash
dotnet test --solution RepoWatch.slnx
```

Tests use xunit v3 on Microsoft.Testing.Platform (opted in via `global.json`), so `dotnet test` takes `--solution` / `--project` options rather than a positional path. Headless UI tests write screenshots to `artifacts/screenshots/` (not committed).

## Releases

`scripts/publish-windows.ps1` builds the release: locked restore, Release build and tests, a self-contained `win-x64` publish, then `artifacts/release/RepoWatch-<version>-win-x64.zip` and its `.sha256`.

```bash
pwsh scripts/publish-windows.ps1
```

- **Reproducible:** deterministic compilation with CI path mapping, locked package versions, no debug symbols, and a zip whose entries are sorted ordinally and stamped with the commit time. The same commit gives the same SHA-256 from any checkout path with the same .NET SDK and PowerShell versions, which the script prints (`global.json` rolls forward to newer 10.0 SDKs, and zip compression comes from the runtime PowerShell runs on). The script warns when the working tree has uncommitted or untracked files; such a build must not be released.
- **Version:** `Version` in `Directory.Build.props`. The commit is added automatically and shown in About and in diagnostics.
- **Symbols:** release builds have none. Avalonia's XAML compiler would record absolute build paths in them, breaking reproducibility; logged stack traces keep method names but not line numbers.
- **Signing:** not done yet. Certificates and keys must never be committed (`*.pfx`, `*.snk` and `*.pem` are ignored); a later signing step must take them from secret storage.
- **Publishing a GitHub release:** bump `Version`, add the version's section to `CHANGELOG.md`, merge, then push a `vX.Y.Z` tag. The Release workflow builds, tests and publishes the zip, its `.sha256` and the notes. See [docs/updates.md](docs/updates.md). Pushing the tag is the maintainer's decision; the manual CI workflow only uploads the zip as a workflow artifact.
- **`release.json`** in the zip marks a release folder; only such a copy can update itself.
- **Before a release,** run [docs/validation-checklist.md](docs/validation-checklist.md) against the published zip, not a debug build.
- `scripts/make-icon.ps1` regenerates `src/RepoWatch.Desktop/Assets/RepoWatch.ico`, used for the executable, windows, tray and notifications.

### Which checks need what

| Check | Requirement |
| --- | --- |
| Restore, build, unit tests | Any OS with the .NET 10 SDK |
| Release publish | Windows with the .NET 10 SDK and PowerShell 7 |
| Running the desktop app | A desktop session; Windows is the delivery target |
| Tray, window materials, Credential Manager, startup registration, notifications, global shortcut | Windows |
| Sign-in and live GitHub smoke tests | A registered GitHub App client ID and a GitHub account |
| Relay tests (in-memory relay, end to end) | Any OS with the .NET 10 SDK |
| Live mode with real GitHub webhooks | A deployed relay (HTTPS) and the GitHub App's webhook configured; see [docs/relay.md](docs/relay.md) |

## Configuration

Configuration holds only **public** deployment values. Secrets never belong here. Sources, in increasing precedence:

1. Built-in defaults.
2. `appsettings.json` next to the executable.
3. A per-user override file: `%LOCALAPPDATA%\RepoWatch\repowatch.config.json`.
4. Environment variables prefixed `REPOWATCH__`, with `__` as the section separator, e.g. `REPOWATCH__GitHub__ClientId`.

| Key | Default | Meaning |
| --- | --- | --- |
| `GitHub:ClientId` | empty (the shipped `appsettings.json` sets it) | Public client ID of the Repo Watch GitHub App. Empty means sign-in is unavailable. |
| `GitHub:AppSlug` | empty (the shipped `appsettings.json` sets it) | The app's URL slug, used for the "Grant repository access" link. |
| `GitHub:WebBaseUrl` | `https://github.com` | GitHub web base URL (https only). |
| `GitHub:ApiBaseUrl` | `https://api.github.com` | GitHub REST base URL (https only). |
| `Polling:ActiveWorkflowSeconds` | 20 | Target refresh for running workflows (5–3600). |
| `Polling:PullRequestSeconds` | 90 | Target PR refresh (5–3600). |
| `Polling:IssueSeconds` | 120 | Target issue refresh (5–3600). |
| `Polling:QuietSeconds` | 180 | Target refresh for idle repositories (5–3600). |
| `Cache:RetentionDays` | 30 | How long cached repository data and notification history are kept (1–365). |
| `Cache:MaxCachedResponses` | 2000 | Most cached REST responses per account (100–100000). |
| `Relay:BaseUrl` | empty | Optional live-update relay ([docs/relay.md](docs/relay.md)). https, or `http://localhost` for development. Empty: polling only. |
| `Updates:Repository` | `PantaKoda/repo-watch` | Public repository whose GitHub releases are Repo Watch's updates (owner/name). Empty turns update checks off. See [docs/updates.md](docs/updates.md). |
| `Updates:CheckIntervalHours` | 24 | Hours between automatic update checks (0–168; 0 = only when asked). |

Unknown keys, wrongly typed values, malformed JSON and invalid values stop startup with a window listing each problem, the setting to change and the files that were read. Problems are also logged.

`REPOWATCH_DATA_DIR` (single underscore; not a configuration key) relocates the per-user data folder, which is useful for portable use and isolated testing.

### Local data

| Path (default) | Contents |
| --- | --- |
| `%LOCALAPPDATA%\RepoWatch\repowatch.config.json` | Optional configuration override |
| Windows Credential Manager, `RepoWatch:github/<host>/<userId>` | GitHub access and refresh tokens. The only place tokens are stored. Removed on sign-out. |
| `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, value `RepoWatch` | Only while **Start Repo Watch when I sign in** is on; removed when it is turned off. Also visible in Task Manager → Startup apps. |
| `HKCU\Software\Classes\AppUserModelId\RepoWatch.Desktop` | The app identity Windows needs to show Repo Watch notifications (display name only). |
| `%LOCALAPPDATA%\RepoWatch\repowatch.db` | SQLite database: settings, plus per-account caches of watched repositories' last data and REST ETags (private repository content). Also the notification history (event keys: repository IDs, branch names, commit SHAs, run attempts; no titles). The caches are removed for an account on sign-out and pruned after 30 days (configurable) or when a repository is no longer watched. Never contains tokens. |
| `%LOCALAPPDATA%\RepoWatch\diagnostics\` | Diagnostics archives you create from Settings → Export diagnostics: versions, states, counts and redacted logs; no tokens, codes, repository names or content. |
| `%LOCALAPPDATA%\RepoWatch\updates\` | A downloaded update while it is being installed; removed afterwards. |
| `<install folder>.previous` | The version before the last in-app update, kept for a manual rollback until the next update. |
| `%LOCALAPPDATA%\RepoWatch\logs\` | Daily rolling logs, newest 7 files kept. Tokens and private content must never be logged. |

## Repository layout

```
src/RepoWatch.Core      Domain models, policies, configuration contracts (no UI/OS dependencies)
src/RepoWatch.GitHub    GitHub auth/API clients, synchronization, relay client
src/RepoWatch.Desktop   Avalonia app, storage, settings, platform adapters
src/RepoWatch.Relay     Optional webhook relay (ASP.NET Core): signed deliveries, sessions, server-sent events
tests/                  Focused tests
scripts/                Release publish and icon generation
docs/                   Architecture, progress, GitHub App and relay setup, validation checklist
```

See [docs/architecture.md](docs/architecture.md).
