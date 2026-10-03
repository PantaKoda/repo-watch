# Live updates: the relay (optional)

> **Project decision (4 Oct 2026): the published Repo Watch does not use a relay.** Webhooks belong to a GitHub App, so every user of the shared app would depend on one maintainer-run server for uptime, cost and privacy (it would receive event payloads about other people's private repositories). Repo Watch therefore ships polling: each user polls with their own token and rate limit, conditional requests make unchanged answers free, and bringing the widget to the front refreshes anything older than 30 seconds. The relay stays here as an **advanced, self-hosted option**: a person or organization who registers their own GitHub App and hosts their own relay can point `Relay:BaseUrl` at it.

Repo Watch works with polling alone. The relay adds near-real-time updates. GitHub sends webhooks to the relay, and the relay tells connected desktop apps *what* changed: a repository and a part (Actions, pull requests, issues, metadata). Each app then reads the current state from GitHub itself. Desktop apps only make outbound connections; they need no open port.

```
GitHub ──webhook (HTTPS, signed)──► relay ──server-sent events (HTTPS, per-session)──► desktop app ──REST/GraphQL──► GitHub
```

When the relay is unreachable, the app polls exactly as without it, and the badge shows **Polling**.

Live coverage is per repository:
- Only the repositories the relay's session allows poll more slowly (×4, to reconcile).
- The badge shows **Live** only when every watched repository is covered.
- A stream that sends nothing for 60 seconds counts as dead (the relay sends a keep-alive every 20). Waking from sleep and a network change also restart the stream. A dead connection therefore never keeps the badge on Live.

A connected stream still doesn't mean every value is fresh: every part keeps its own freshness label.

## What the relay does

| Concern | How |
| --- | --- |
| Authenticity | Verifies `X-Hub-Signature-256` (HMAC-SHA256 of the raw bytes with the webhook secret, constant-time compare). Unsigned or invalid requests get 401. |
| Durability | Stores each delivery in SQLite **before** answering (202, well within GitHub's 10-second limit). Processing happens in the background. |
| Duplicates | `X-GitHub-Delivery` is unique: a redelivery is answered 200 and processed once. |
| Failures | Failed processing is retried with exponential backoff (up to `MaxDeliveryAttempts`). A malformed payload is dropped at once. Finished deliveries are kept `DeliveryRetentionDays`. |
| Untrusted payloads | Only IDs and the action are read. Nothing in a payload is ever executed or forwarded as content. |
| Who may listen | `POST /sessions` with the user's GitHub token in the `Authorization` header. The relay asks GitHub who the user is and which of the requested repositories this user *and* the app can access, counting only installations of Repo Watch's own app (`AppId`). It grants only those. The token is used for those requests only: it is never stored, logged or accepted in a URL. If GitHub fails part-way, the session fails (502) rather than silently covering less. If nothing is allowed, the answer is 403. |
| Sessions | Random session tokens, stored as hashes, valid `SessionMinutes` (default 15). The app renews about a minute before expiry. |
| Revocation | `installation` deleted or suspended, `installation_repositories` removed, `repository` deleted, and `github_app_authorization` revoked all remove the affected repositories from sessions, or end the user's sessions, immediately. |
| Ordering and gaps | Events carry increasing sequence IDs. A reconnect sends `Last-Event-ID` and gets the missed events in order from a bounded buffer (`ReplayCapacity`). A gap beyond the buffer, an ID from a previous relay run, or a client too slow to keep up gets `reset`, and the app refreshes everything. |

The relay never uses GitHub's Events API (it is delayed by minutes and is not a live feed).

## Endpoints

| Method and path | Purpose |
| --- | --- |
| `POST /webhooks/github` | GitHub webhooks (the only public endpoint GitHub calls). |
| `POST /sessions` | Desktop: `Authorization: Bearer <GitHub user token>`, body `{"repositoryIds":[…]}`. Returns `{sessionToken, expiresAt, allowed, rejected}`. |
| `GET /events` | Desktop: `Authorization: Bearer <session token>`, optional `Last-Event-ID`. Server-sent events: `invalidate` `{repositoryId, parts}`, `revoked`, `reset`, `expired`, and keep-alive comments. |
| `GET /healthz` | Liveness. |

## GitHub App webhook settings (maintainer)

These are changes on the GitHub App, so they are the maintainer's to make; Repo Watch never changes them.

1. **Webhook → Active**: on.
2. **Webhook URL**: `https://<your relay host>/webhooks/github`.
3. **Webhook secret**: a long random value, for example `openssl rand -hex 32`. Store it only in the relay's secret configuration.
4. **Subscribe to events**, exactly these: *Workflow run*, *Workflow job*, *Check run*, *Check suite*, *Status*, *Pull request*, *Pull request review*, *Issues*, *Repository*. *Installation* and *installation repositories* events are always sent to GitHub Apps. The *GitHub App authorization* event (`github_app_authorization`) is always sent as well.
5. No new permissions are needed: the read permissions from [github-app-setup.md](github-app-setup.md) cover these events.

## Configuration

Everything is in the `Relay` section, set through `appsettings.json` or environment variables (`Relay__WebhookSecret`, …).

| Setting | Default | Notes |
| --- | --- | --- |
| `WebhookSecret` | none | **Required**, at least 16 characters. The relay refuses to start without it. Secret store only, never in the repository. |
| `AppId` | none | **Required**: the GitHub App's numeric App ID (public, shown on the app's settings page). Tokens issued to other apps open no session. |
| `DatabasePath` | `data/relay.db` | SQLite file for deliveries. Use a persistent volume. |
| `GitHubApiBaseUrl` | `https://api.github.com` | Used only to confirm sessions. |
| `SessionMinutes` | 15 | 1–60. |
| `ReplayCapacity` | 1000 | Events kept for reconnects. |
| `MaxDeliveryAttempts` | 5 | |
| `DeliveryRetentionDays` | 7 | |
| `KeepAliveSeconds` | 20 | Keeps proxies from closing idle streams. |

The desktop app's own setting is `Relay:BaseUrl` in its `appsettings.json` or user override file, for example `https://relay.example.com/`. When it is empty, the app polls only. Plain `http://` is accepted only for a relay on this machine (`localhost`), during development.

## Local development

```bash
dotnet run --project src/RepoWatch.Relay --urls http://127.0.0.1:5088
```

Set `Relay__WebhookSecret` (for example `local-development-secret-0123`) and `Relay__AppId` in the environment first. To deliver a test webhook, sign the exact body:

```bash
body='{"action":"completed","repository":{"id":123}}'
sig="sha256=$(printf '%s' "$body" | openssl dgst -sha256 -hmac "$Relay__WebhookSecret" | cut -d' ' -f2)"
curl -i http://127.0.0.1:5088/webhooks/github -H "X-GitHub-Event: workflow_run" -H "X-GitHub-Delivery: $(uuidgen)" -H "X-Hub-Signature-256: $sig" -d "$body"
```

To receive real GitHub webhooks during development you need a public HTTPS URL that forwards to the relay, such as a tunnel. Setting one up is your decision; Repo Watch doesn't create one.

## Deployment (prepared, not performed)

`src/RepoWatch.Relay/Dockerfile` builds a small image that runs as a non-root user on port 8080, with `/app/data` for the database:

```bash
docker build -f src/RepoWatch.Relay/Dockerfile -t repowatch-relay .
docker run -p 8080:8080 -e Relay__WebhookSecret=… -e Relay__AppId=… -v repowatch-relay-data:/app/data repowatch-relay
```

Put it behind HTTPS (a reverse proxy or the platform's TLS), point the GitHub App webhook at it, and set the desktop `Relay:BaseUrl`. One instance is enough. The replay buffer and sessions are in memory, so after a restart clients get `reset` and refresh everything, and no deliveries are lost because they are in SQLite.

## Limits and honest notes

- **Latency** measured in-process (signed webhook → in-memory relay → desktop link → targeted refresh request): about 20 ms. The real end-to-end time adds GitHub's webhook delivery and the network, and depends on the deployment; it was not measured, because no relay has been deployed.
- **Restarts:** sessions and the replay buffer don't survive a relay restart. Clients reconnect, renew their session, and reconcile.
- **Single instance:** several instances behind a load balancer would need shared session and event state, which isn't implemented.
- **Rate limits:** each session creation makes a few GitHub API calls with the user's token (user, installations, repositories), about every 14 minutes per running app. A first connect or a `reset` refreshes every watched repository once. A renewal or reconnect resumes from `Last-Event-ID` without a full refresh.
- **Collaborator, team or organization removals:** these don't end a session early. Subscribing to the events that would report them (`member`, `membership`, `organization`) needs organization *Members* permission, which Repo Watch doesn't request. So a user removed from a repository while the app stays installed can keep receiving invalidations, which carry only IDs and parts, until the session expires (at most 15 minutes, or `SessionMinutes`). The next session re-checks access with GitHub.
