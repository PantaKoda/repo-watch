# Release validation checklist (Windows)

Run this against the **published zip** (`scripts/publish-windows.ps1`), extracted to a fresh folder, not a debug build. Use a Windows account where Repo Watch has not run before, or point `REPOWATCH_DATA_DIR` at an empty folder. Record the version/commit from Settings → About, the Windows version and display scale, and anything that failed. Items marked *(maintainer)* need the GitHub App owner.

## Build

- [ ] `pwsh scripts/publish-windows.ps1` passes restore, build, tests and publish on a clean checkout of the release commit, without the "uncommitted changes" warning.
- [ ] The SHA-256 printed matches `RepoWatch-<version>-win-x64.zip.sha256`. A build of the same commit in another folder, or on another machine with the same .NET SDK and PowerShell versions (printed by the script), prints the same hash.
- [ ] The zip contains `RepoWatch/RepoWatch.exe` and `RepoWatch/appsettings.json` with the production client ID and slug, and no `.pdb`, `appsettings.Local.json` or other secrets.
- [ ] Explorer shows the Repo Watch icon on `RepoWatch.exe`; *Properties → Details* shows the version.

## First run

- [ ] Starting `RepoWatch.exe` shows the widget, the tray icon and the onboarding window, with no configuration error.
- [ ] **Sign in:** the device code appears with *Copy code* and *Open GitHub*. The browser opens github.com/login/device. Approving signs in and shows the username and avatar. The app never asks for a password or a token.
- [ ] Cancel, deny on GitHub, and letting the code expire each show a clear state with *Try again*.
- [ ] **Grant access:** *Grant access on GitHub* opens the app's installation page. The app is public (any account can install it); check with a second GitHub account when possible.
- [ ] **Choose repositories:** personal, organization and private repositories that GitHub granted are listed (all pages), with search, owner filter, *Show selected* and a selected count. Nothing is selected automatically.
- [ ] **Appearance:** theme, material and background changes apply to the widget at once.
- [ ] **Open widget:** the chosen repositories appear with Actions, pull requests and issues, each with its own freshness. Spot-check against GitHub: a failed run, an open pull request and the open issue count.

## Daily use

- [ ] The tray icon toggles the widget. The tray menu's *Show widget*, *Pause monitoring*, *Settings…* and *Quit Repo Watch* work.
- [ ] Ctrl+Alt+R hides and shows the widget. A second `RepoWatch.exe` start shows the running instance and exits.
- [ ] Drag, resize, *Lock the widget's position* and *Keep the widget above other windows* work. Placement survives a restart. A widget moved off-screen comes back after restart.
- [ ] Keyboard: Tab, arrow keys, Enter, Esc, F5 and Ctrl+, work and the focus indicator is visible.
- [ ] Links (runs, pull requests, issues) open the right GitHub page in the browser.
- [ ] *Show a test notification* shows a toast. A real CI failure, review request or merge notifies once. Quiet hours suppress notifications. No burst of old notifications after a restart.
- [ ] Unplug the network: the header shows `Offline` and data is labeled stale. Reconnect: data refreshes without a restart.
- [ ] *Start Repo Watch when I sign in* and *Start minimized to the tray* take effect at the next Windows sign-in.
- [ ] Settings → About shows the version and commit. *Check for updates* opens the releases page. *Export diagnostics* writes an archive without tokens, codes or repository names.
- [ ] Appearance at 100%, 125% and 150% display scale: text stays crisp and readable in light and dark, solid and frosted.

## Updates

- [ ] *(maintainer)* Pushing tag `vX.Y.Z` runs the Release workflow and creates a release with the zip, its `.sha256` and the CHANGELOG notes.
- [ ] Settings → *Check for updates* on the newest version says there is no newer release.
- [ ] With an older release installed, the widget shows **UPDATE** (at most 30 s after start); the window lists every newer release's notes.
- [ ] *Install update* downloads, restarts into the new version (About shows it, the widget says "Updated to …"), keeps the sign-in, and leaves `<folder>.previous`.
- [ ] A copy run from a build folder (not a release zip) explains that it can't update itself.

## Restart and sign-out

- [ ] Quit and start again: still signed in, the same repositories and order, and cached data shown (labeled) until the refresh completes.
- [ ] *Sign out*: monitoring stops, the account's cached data is deleted, and Credential Manager has no `RepoWatch:github/…` entry. Signing in again restores the watchlist.
- [ ] Revoke the app on GitHub (*Review access on GitHub*) while signed in: the widget shows `Reconnect required` and stops retrying. *Sign in again* recovers.

## Optional: live mode (relay)

- [ ] *(maintainer)* With a deployed relay and the webhook configured ([relay.md](relay.md)), the header shows `Live`. Pushing a commit updates the widget within seconds. Record the observed latency.
- [ ] Stopping the relay switches the header to `Polling` and data keeps refreshing.

## Clean up

- [ ] Turn off start at login, sign out, quit, and delete the extracted folder and the data folder.
