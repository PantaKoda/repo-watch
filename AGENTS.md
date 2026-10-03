# AGENTS.md — Repo Watch

## Mission and scope

Implement **Repo Watch**, a Windows-first desktop widget for monitoring GitHub Actions, pull requests and issues. Preserve a shared core and UI for later macOS/Linux releases. Use C#, a supported .NET LTS version and Avalonia. Build a real desktop application with GitHub sign-in, a repository picker, persistent settings, transparent/opaque visual modes, and both polling and webhook-assisted monitoring.

This file is the ordered implementation contract. Place it at the implementation repository root. It is self-contained; `repo-watch-design.md`, when available, is the design rationale. The requirements here supersede the earlier PAT-first authentication proposal and opaque-only mockup. The mockup contains illustrative data, not live repository state.

The current delivery target is Windows. Later platform releases and optional features are explicitly identified below; do not present them as already implemented.

## How to work

1. Read the existing repository instructions, code and build configuration before editing. Preserve unrelated work.
2. Execute the numbered stages below in order. Complete each stage's acceptance checks before starting the next. Continue through authorized stages without asking for approval after routine changes.
3. At the start of each session, read this file and `docs/PROGRESS.md`. Resume the first incomplete stage. Create that progress file during Stage 01 if it does not exist.
4. Record each stage as `pending`, `in_progress`, `completed` or `blocked`, with implemented behavior, checks actually run, remaining limitations and the next concrete task. Keep at most one stage in progress.
5. If external credentials, app registration or a required operating system are unavailable, complete independent work and record the exact blocker. Use clearly labeled fixtures for development; never mark live authentication, native integration or delivery latency as verified from mocks.
6. Keep the application runnable after each stage. Favor small changes and targeted tests for authentication, data correctness, synchronization and persistence. Do not create tests that only mirror trivial implementation details.
7. Do not introduce a new stack, broad abstraction framework, external backend or extra processes without a demonstrated requirement. A small relay is required only for the live mode in Stage 10.
8. Do not deploy, publish a release, install a GitHub App on an account, or mutate repository resources unless that external action is authorized. Preparing code, local builds and reviewable setup documentation is part of the work.
9. Work sequentially. Do not delegate to additional agents unless the user requests it.

## Product requirements that must remain true

- The main UI is a small floating widget, approximately 400 device-independent pixels wide, with a compact summary mode, expanded details and a tray/menu-bar entry.
- Users sign in through GitHub in their system browser. Personal access token entry is not the normal onboarding flow; never request a GitHub password inside the app.
- Users choose, add, remove and reorder the repositories they monitor. Support personal and organization repositories the current identity and GitHub App are authorized to access, including private repositories.
- GitHub-granted access and the widget's watchlist are separate: permission does not automatically add a repository to the widget.
- Users can choose opaque, transparent or frosted visuals where supported. Text, icons, focus indicators and essential controls remain readable and fully opaque.
- Actions, PRs and issues retain independent state and freshness. A successful CI result is not proof of connectivity, merge eligibility or absence of outstanding work.
- Use read permissions for monitoring. Open GitHub in the browser for merges, comments, reviews and workflow reruns in this version.
- Polling works without a relay. Live mode uses webhooks and a relay; it must fall back to polling when disconnected.
- Use actual platform capabilities. A missing tray, blur effect or global shortcut must not make the application unusable.

## Technical boundaries

| Project | Owns |
| --- | --- |
| `src/RepoWatch.Core` | Domain models, status aggregation, attention and notification policies, service contracts. No Avalonia or OS-specific dependencies. |
| `src/RepoWatch.GitHub` | GitHub authentication and API clients, pagination, conditional requests, synchronization and relay client. |
| `src/RepoWatch.Desktop` | Avalonia/MVVM views, local SQLite storage, settings and platform adapters. Keep adapters in clearly separated namespaces/files. |
| `src/RepoWatch.Relay` | Stage 10 webhook receiver, durable delivery handling and authenticated SSE. |
| `tests/` | Focused core, HTTP-contract, persistence and relevant UI/integration checks. |

Use `HttpClient`, `System.Text.Json`, CommunityToolkit.Mvvm and Microsoft.Data.Sqlite unless the repository already has an equivalent justified dependency. Pin compatible stable versions. Prefer dependency injection at composition roots. UI code observes state and issues commands; it must not contain HTTP polling loops.

Use interfaces for credentials, external-browser launch, tray, notifications, startup registration, shortcuts and window materials. Keep shared projects free of unconditional Windows-only calls. Do not implement a generic plugin system.

## Stage 01 — Establish the project and development baseline

1. Inspect existing files and implement only missing scaffolding. Record the target SDK, Avalonia version and supported Windows baseline in the README.
2. Create the solution and initial Core, GitHub, Desktop and test projects. Add nullable analysis, deterministic builds, logging and centralized validated configuration.
3. Create `docs/PROGRESS.md`, `docs/architecture.md` and documented restore/build/run/test commands. Explain which checks require Windows or configured GitHub access.
4. Add CI definitions for build and meaningful tests on Windows; add shared-code build checks on macOS/Linux when the CI environment supports them. Do not claim those platforms are release-ready.

**Acceptance:** a clean checkout restores and builds; the desktop opens on an available target; configuration failures are actionable; no credentials are committed.

## Stage 02 — Model repository state and settings

1. Use stable GitHub repository IDs plus host and authenticated account identity for storage keys. Owner/name are display/routing metadata and can change.
2. Model workflows/runs/jobs, PRs, reviews/checks and issues separately. Include commit SHA, run attempt, relevant scope, URLs and timestamps.
3. Represent `unknown`, absent checks, queued, running, successful, failed, cancelled, skipped and neutral outcomes without silently equating them.
4. Track data availability, last successful refresh and error state per resource. Separate cached data from current data. Do not clear valid values because another endpoint failed.
5. Define persisted settings for watchlists, ordering, workflow/branch filters, appearance, notifications, window placement and startup behavior. Include a schema version and migration strategy.

**Acceptance:** focused tests cover current-commit aggregation, superseded run attempts, independent resource failures and settings round trips.

## Stage 03 — Build the functional desktop shell

1. Implement the widget, compact bar, expanded repository details and a separate settings/onboarding window. Use labeled fixtures in explicit demo mode only.
2. Provide Actions, Pull requests and Issues tabs, fresh/stale/offline indicators, manual refresh, empty/loading/error states and browser links.
3. Implement dragging, resizing, optional always-on-top, focus behavior and a working tray menu with Show, Settings and Quit. If no usable tray exists, keep normal window recovery available.
4. Preserve placement per monitor and restore off-screen windows after display changes. Save dimensions in device-independent units.
5. Keep lists stable during pointer/keyboard interaction. Do not steal focus or reorder a focused row when a status changes.

**Acceptance:** all visible controls work, keyboard navigation is usable, a 320-pixel-wide layout remains readable, and a hidden widget can always be recovered.

## Stage 04 — Implement Sign in with GitHub

1. Configure a **GitHub App with device flow enabled**. App registration is a one-time maintainer task; normal end users sign in and grant access without registering their own app. Document maintainer setup in `docs/github-app-setup.md`; configure the public client ID and installation URL. Keep external registration/installation actions user-controlled.
2. Implement the device authorization flow [1]: request a device code, show the user code with Copy/Open GitHub actions, launch the system browser and poll at the returned interval. Support pending, slow-down, denied, expired, cancelled and retry states.
3. Resolve the signed-in identity through GitHub after token acquisition. Show the username and avatar; isolate settings/cache by account and host. Start with one active account while preserving these boundaries for future multi-account support.
4. Enable expiring tokens. Implement refresh-token rotation and serialize refresh per account. GitHub documents that tokens obtained through device flow can refresh without a client secret [2]. Read returned expiry values and recover through sign-in if refresh fails; do not disable expiry to avoid implementing renewal.
5. Keep access/refresh tokens in Windows Credential Manager, macOS Keychain or a Linux Secret Service adapter as each platform is delivered. Never put tokens in SQLite, settings exports, logs, URLs or the clipboard. If secure storage is unavailable, offer a clearly identified session-only mode rather than plaintext persistence.
6. Never embed a GitHub App client secret or private key in the desktop executable. The normal desktop flow requires neither. Any server-only app credentials belong in relay secret configuration.
7. On sign-out, cancel API/relay activity, invalidate in-flight results, remove local credentials, stop notifications and clear that account's cached private content. Allow non-secret watchlist preferences to remain for a later sign-in. Provide a link to GitHub's authorization management; local sign-out is not a claim of server-side revocation.

**Acceptance:** verify successful login and restart, cancellation/denial, expired device code, token renewal, revoked access and sign-out cleanup. An expired session becomes a visible reconnect state instead of an endless retry loop.

## Stage 05 — Add repository access and the watchlist picker

1. Implement onboarding: **Sign in → Grant repository access → Choose repositories → Appearance → Open widget**. Users with an existing installation can skip the grant-access step.
2. Use accessible GitHub App installations and their repositories, following all pagination [3]. Combine personal and organization entries, deduplicate by repository ID and display owner/name, privacy, organization and archived status.
3. Add search, owner/organization filters, checkboxes, selected count, a Show selected view and visible Add/Remove controls. Bulk selection must state whether it applies to the filtered results or the current page.
4. Persist the watchlist and manual order per account. Let users choose attention-first or manual ordering, default branch/selected PR scopes, and selected workflows. Do not subscribe to every repository automatically.
5. Distinguish “GitHub has not granted this app access” from “not selected in this widget.” Provide Manage access/Open GitHub and Refresh list actions. Handle pending organization approval, SSO requirements, suspended installations and permissions changes without claiming the repository does not exist.
6. Keep the repository manager available in settings after onboarding. Removing a repository cancels its scheduled work/subscriptions and notifications immediately. Local deselection must not uninstall the GitHub App or revoke its GitHub permissions.
7. Handle repository rename/transfer using IDs. Treat loss of access as an explicit state and stop using cached private content until access is restored.

**Acceptance:** selected repositories survive restart; unselected repositories generate no routine monitoring calls; private/org repositories and multiple list pages work; empty watchlists show an Add repositories action.

## Stage 06 — Fetch and normalize real GitHub data

1. Replace demo data with API adapters. Start with REST; use GraphQL where it reduces calls or supplies useful review fields. Verify exact endpoints and least-privilege permissions against current GitHub documentation.
2. Request repository read permissions for Metadata, Actions, Pull requests, Issues and Checks; include Commit statuses when consuming legacy status checks. Add Contents or organization/team permissions only for a documented feature that requires them.
3. Load latest relevant runs and fetch jobs only for expanded or active runs. Use the current branch/PR/check commit and latest attempt; keep default-branch health distinct from open-PR failures.
4. Keep review requests, review outcomes and mergeability separate. When required-check or protection information is unavailable, show unknown or individual states; do not invent “Ready to merge.” Team review requests are “for me” only when membership is established.
5. Exclude entries containing `pull_request` from REST issue counts [4]. Follow pagination or use reliable API counts; label partial data instead of treating a first page as the total.
6. Handle 401, 403, 404, disabled Actions, archived repositories, missing workflows and unavailable individual features. An inaccessible endpoint must not erase unrelated sections.
7. Open API-provided web URLs through a validated HTTPS/browser-launch adapter. Treat titles and other repository content as untrusted display text; never render them as executable markup or commands.

**Acceptance:** contract fixtures cover success and failure; a configured live smoke check compares a chosen repository with GitHub, including a failed run, an open PR and issue counts.

## Stage 07 — Add durable caching and efficient synchronization

1. Implement one bounded, account-aware request scheduler. Coalesce duplicate work, serialize GitHub requests where appropriate and prioritize active runs and visible details.
2. Start with 15–30-second active workflow refresh, 60–120-second PR/issues refresh and 2–5-minute quiet refresh. These are configurable targets subordinate to server headers and a request budget, not latency guarantees.
3. Support stable conditional REST requests with ETags. Honor rate-limit reset, retry-after and poll-interval headers; apply bounded exponential backoff with jitter [5]. Never treat conditional requests as exemption from every limit.
4. Store snapshots, successful timestamps, ETags and notification history in SQLite. Apply account isolation, schema migrations and configurable retention. Keep secrets outside the database.
5. Refresh on wake and network recovery. Keep stale values labeled during network errors; pause protected-data access for revoked credentials or lost permission. Reject responses from removed repositories or obsolete account/refresh generations.
6. Slow background work when hidden or on battery. Keep user-visible refresh timestamps independent of notification timestamps. Provide a Pause monitoring control.

**Acceptance:** simulate offline operation, wake/reconnect, API throttling, out-of-order responses, account changes and partial failures. Confirm no request storms or false empty states.

## Stage 08 — Implement modern visuals and real transparency

1. Create reusable visual tokens for spacing, surfaces, borders, radii, typography and status colors. Use restrained Fluent-inspired styling: rounded corners, a subtle border/shadow, compact rows and clear hierarchy.
2. Add System/Light/Dark themes, a restrained accent option, comfortable/compact density and background opacity settings. Choose a readable frosted surface in Auto mode when supported; otherwise choose Solid.
3. Offer **Auto, Solid, Transparent and Frosted/Acrylic** materials; expose Mica as a separate Windows option where available. Mica is a system backdrop, not a promise of seeing live windows behind the widget.
4. Apply opacity to background/material layers only. Do not lower whole-window opacity, which would also fade text. Support a user-controlled background range such as 20–100%; keep tooltips, menus, settings and errors sufficiently opaque.
5. Use the pinned Avalonia version's supported transparency APIs and check the actual achieved material [6]. Encapsulate native effects behind a window-material adapter. Record platform limitations rather than promising identical blur on every compositor.
6. Provide an opaque fallback for disabled OS transparency, high-contrast mode, unsupported materials, remote sessions or power-saving restrictions. Respect reduced motion; avoid permanent animated effects that waste idle CPU/GPU.
7. Transparency must not silently enable click-through. Keep normal hit testing. Defer a separate click-through feature unless a tested recovery shortcut and tray command exist.

**Acceptance:** visually inspect light/dark and solid/transparent/frosted modes over bright, dark and busy backgrounds on Windows; verify text remains opaque, DPI scaling works and the fallback is readable. Save screenshots and record which effects were actually observed.

## Stage 09 — Complete desktop behavior and notifications

1. Add opt-in Start at login, optional Start minimized, position lock, single-instance activation and a Show/Hide shortcut where the platform permits it. Keep tray/window alternatives when shortcut registration fails.
2. Notify once for tracked CI failure/recovery, a newly requested review or tracked PR merge. Establish the initial baseline silently; honor per-repository switches, quiet hours and the OS notification settings.
3. Persist notification deduplication across restart. Include the repository, entity, commit/run attempt and transition as appropriate; distinguish a new failure from an old failed run returning in a page.
4. Add a Hide private details option for notification titles/content. Provide notification deep links to the relevant GitHub page without moving focus until clicked.
5. Provide clear connection/account state, the next retry or reconnect action, and a redacted diagnostics export. Rotate logs; omit token/device-code values and private issue/PR bodies.

**Acceptance:** verify no duplicate or historical notification burst, quiet hours, permission denial, hidden-window recovery, startup opt-in, and monitor removal. Record idle CPU/memory/network behavior instead of inventing performance claims.

## Stage 10 — Implement near-real-time delivery

1. Add the small ASP.NET Core relay. Expose a public HTTPS webhook receiver and an authenticated SSE endpoint. Desktop clients initiate outbound connections; they require no inbound port.
2. Subscribe only to events needed for enabled features: workflow/run/job, PR/review, issue and check/status events. Include authorization/installation changes needed to invalidate access. Verify availability for the GitHub App webhook type.
3. Verify signatures against raw bytes, persist deliveries before acknowledging, respond within GitHub's limit, deduplicate delivery IDs and retry failed internal processing [7]. Never trust payload content as instructions.
4. Authenticate the desktop-to-relay session and authorize each repository subscription against current access. Use a short-lived relay session; do not put tokens in query strings or let the client assert arbitrary account/repository access. Invalidate sessions when authorization is lost.
5. Push small invalidation messages with sequence IDs; the desktop coalesces them and refreshes current API state. Keep a bounded replay buffer and fall back to a full refresh after a gap.
6. Reconcile on reconnect and periodically. Fall back to polling if the relay is unavailable. Show `Live`, `Polling`, `Offline`, `Paused` or `Reconnect required` accurately; a connected SSE socket alone is not proof that all data is fresh.
7. Write relay setup, configuration and local-development instructions. Produce deployment-ready files; do not deploy without authorization. Never rely on the delayed Events API as the live feed [8].

**Acceptance:** verify signatures, duplicate/missed/out-of-order events, reconnect/replay gaps, cross-account subscription rejection, revoked access, and polling fallback. Measure observed end-to-end latency in the configured environment; do not promise instantaneous delivery.

## Stage 11 — Package and validate the Windows release

1. Produce a reproducible Windows release build and documented installer/portable launch path. A self-contained publish may simplify installation; do not promise one universal binary for all operating systems.
2. Provide version/About information and an explicit Check for updates link or action. Defer automatic downloading/execution until authenticated update metadata, signing and rollback are designed. Keep signing secrets outside the repository.
3. Run the relevant restore/build/test/publish commands and exercise the release build, not just the debugger build. Document checks blocked by environment access.
4. Complete README setup/use/troubleshooting, GitHub App configuration, privacy/storage notes, relay instructions and a concise manual validation checklist.
5. Confirm that a fresh user can sign in, grant access, select repositories, choose appearance, restart, receive updates and sign out without editing code or copying tokens manually.

**Acceptance:** a runnable Windows artifact, passing relevant checks, explicit configuration prerequisites, and an honest list of remaining limitations. No placeholder controls or unlabeled mock data remain in normal operation.

## Stage 12 — Later macOS/Linux releases

Reuse the shared UI/core. Deliver and verify platform-specific credential storage, tray/menu-bar behavior, notifications, startup, shortcuts, packaging and window-material fallbacks. Linux window placement, blur, tray support and global shortcuts depend on the desktop/compositor; expose capabilities rather than relying on Windows semantics. Test on real target environments before claiming support.

An optional Quickshell frontend can consume the same snapshots through a user-scoped Unix socket from a small local host. Do not duplicate GitHub/authentication logic in QML or make Quickshell a dependency of the Windows/macOS application.

## Defer unless the user expands scope

Simultaneous multi-account dashboards, GitHub Enterprise Server, multiple independent widgets, custom themes, deployments/releases, live log streaming, write actions, click-through overlays, Windows Widgets-board integration and cloud settings sync. Preserve inexpensive boundaries for these; do not build them ahead of the required features.

## Final implementation handoff

Report implemented stages, actual commands/checks run, artifact paths, how to launch, configuration prerequisites, material limitations and the next incomplete stage. Distinguish a build check from a real OS/authentication/relay smoke test. Keep `docs/PROGRESS.md` accurate.

## Primary references — verify again when implementation begins

1. [GitHub App user access and device flow](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/generating-a-user-access-token-for-a-github-app)
2. [GitHub App token renewal](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/refreshing-user-access-tokens)
3. [GitHub App installation/repository discovery](https://docs.github.com/en/rest/apps/installations)
4. [GitHub REST issues](https://docs.github.com/en/rest/issues/issues)
5. [GitHub REST request guidance](https://docs.github.com/en/rest/using-the-rest-api/best-practices-for-using-the-rest-api)
6. [Avalonia Windows materials and fallbacks](https://docs.avaloniaui.net/docs/platform-specific-guides/windows)
7. [GitHub webhook processing guidance](https://docs.github.com/en/webhooks/using-webhooks/best-practices-for-using-webhooks)
8. [GitHub Events API limitations](https://docs.github.com/en/rest/activity/events)

Design baseline updated: 3 October 2026.
