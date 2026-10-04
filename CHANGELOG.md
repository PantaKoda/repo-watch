# Changelog

What changed in each Repo Watch release. The section for a version becomes its GitHub release notes, which the app shows in its update window, so write for people using the app.

## [Unreleased]

## [0.3.0] - 2026-10-04

### Added
- **Uninstall from inside the app.** Settings › About › *Uninstall Repo Watch…* shows exactly what will be removed and removes it with one click. Choose *Remove everything* (nothing is left on your PC: program, settings, cache, logs, sign-in, startup entry, notifications, shortcuts) or keep only your settings and repository list for a later reinstall. It can open GitHub so you can also revoke Repo Watch's access to your account.
- **Activity at a glance:** repository rows show chips for running workflows, failing CI, open PRs and issues. A row that changes glows briefly and keeps a small dot until you open it.
- **Refresh intervals in Settings:** choose, in seconds, how often running workflows, pull requests and repository details, issues and quiet workflows are checked.
- **Which pull request started a run:** on the Actions tab, a run from a pull request shows "PR #61 · title"; click it to open the pull request. Runs from forks are matched to their pull request too.
- **Comment counts** on each pull request (conversation and inline review comments) and issue. They refresh with the pull request and issue intervals, as Settings now explains, and a new comment makes the repository's row light up like other activity.
- **Resize the widget from any edge or corner**, not only the bottom-right grip.

### Changed
- **Refresh buttons show they're working:** while a refresh you started runs, its icon turns in the theme's accent color (only the color changes with reduced motion).

## [0.2.0] - 2026-10-04

### Added
- **Updates inside the app.** Repo Watch checks GitHub for a newer release once a day. When one exists, an **Update** button appears in the widget header. It opens a window with everything that changed since your version and an **Install update** button. Installing downloads the release, checks it against its published checksum, swaps in the new version and restarts. Your settings and sign-in are kept, and the previous version is kept next to the app folder in case you need to go back.
- **Check for updates** in Settings now really checks. It tells you when there is no newer release, or opens the update window.
- **Find what matters in the list:** a filter by name (Ctrl+F), sorting by *Needs attention*, *Recent activity* or *My order*, and a button that hides idle repositories (nothing open, running or failing).
- Each repository row shows a **Private/Public** tag, when it last had activity, and a button to **open it on GitHub** (Ctrl+Enter on the selected row).

### Changed
- The moving line under the header now appears only while you wait for a refresh you started or the first load, not during routine background checks.
- On very narrow widgets the connection label shrinks instead of sliding under the header buttons.
- The `Updates:ReleasesUrl` setting is no longer used (it is ignored if still set); `Updates:Repository` names where updates come from.

### Fixed
- Starting two copies of Repo Watch at exactly the same moment on a new computer could fail to set up the local database.

## [0.1.0] - 2026-10-03

First Windows release: GitHub sign-in in the browser, choosing repositories, live Actions, pull request and issue status with notifications, tray and start-at-login, frosted or solid appearance, and optional live updates through a relay.
