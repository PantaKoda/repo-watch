# Changelog

What changed in each Repo Watch release. The section for a version becomes its GitHub release notes, which the app shows in its update window, so write for people using the app.

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
