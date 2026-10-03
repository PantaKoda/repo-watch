# GitHub App setup (maintainer task)

Repo Watch signs users in through a **GitHub App with device flow enabled**. The app is registered **once by the maintainer**. End users never register anything; they sign in with the device code shown in Repo Watch and choose which repositories the app may read.

These are user-controlled actions on GitHub. Nothing in this repository performs them automatically.

## 1. Register the app

On GitHub, open **Settings → Developer settings → GitHub Apps → New GitHub App**. For an organization-owned app, use **Organization settings → Developer settings → GitHub Apps** instead.

| Field | Value |
| --- | --- |
| GitHub App name | e.g. `Repo Watch` (the slug in the app's URL becomes `GitHub:AppSlug`) |
| Homepage URL | The project's repository URL |
| Callback URL | Leave empty. The device flow needs no callback |
| Expire user authorization tokens | **Enabled**. Repo Watch renews tokens; never disable expiry |
| Request user authorization (OAuth) during installation | Disabled |
| **Enable Device Flow** | **Enabled**. Without it, sign-in fails with "Device flow is not enabled" |
| Webhook → Active | Disabled for now. Live mode (Stage 10) adds a relay with its own webhook secret |
| Where can this GitHub App be installed? | **Any account**, so other users and organizations can install it |

### Repository permissions (read-only)

| Permission | Access | Used for |
| --- | --- | --- |
| Metadata | Read-only (mandatory) | Repository list, names, privacy |
| Actions | Read-only | Workflow runs and jobs |
| Checks | Read-only | Check runs on commits and pull requests |
| Commit statuses | Read-only | Legacy status checks, and resolving a tracked branch's head commit (`GET /commits/{branch}/status`) without Contents access |
| Issues | Read-only | Issues |
| Pull requests | Read-only | Pull requests, reviews, review requests |

Request **no write permissions**. Merges, comments, reviews and workflow re-runs open GitHub in the browser. Add Contents or organization/team permissions only together with a documented feature that needs them.

No account permissions and no event subscriptions are needed for the polling mode.

## 2. Copy the public identifiers

On the app's settings page, copy:

- **Client ID** (looks like `Iv23li…`). This is **not** the numeric App ID.
- **App slug**, from the public URL `https://github.com/apps/<slug>`.

**Do not** generate a client secret or private key for the desktop app. The device flow and token renewal need neither, and neither may ever be embedded in or shipped with Repo Watch. A private key is needed only by the Stage 10 relay, which keeps it in server-side secret configuration.

## 3. Configure Repo Watch

The client ID and slug are public values. Put them in any configuration source (see README "Configuration"):

```json
{
  "GitHub": {
    "ClientId": "Iv23liXXXXXXXXXXXXXX",
    "AppSlug": "repo-watch"
  }
}
```

- For a release build, set them in the shipped `appsettings.json`.
- For local testing, use `%LOCALAPPDATA%\RepoWatch\repowatch.config.json`, or the environment variables `REPOWATCH__GitHub__ClientId` and `REPOWATCH__GitHub__AppSlug`.

Repo Watch validates the client ID format at startup, so a pasted App ID or token is rejected with an explanation. An unknown client ID produces "GitHub didn't recognise this app's client ID" at sign-in.

## 4. What users do

1. Open Repo Watch, choose **Sign in with GitHub**, and approve the code shown at `https://github.com/login/device`.
2. Install the app on their account or organization and choose repositories at `https://github.com/apps/<slug>/installations/new`. Repo Watch links there in Stage 05.
3. Pick which of those repositories the widget watches. Granted access never adds repositories to the widget automatically.

Users can review or revoke the authorization at <https://github.com/settings/apps/authorizations>. Signing out of Repo Watch removes its local credentials only.

## Verification checklist

| Check | How |
| --- | --- |
| Sign-in works | Sign in; Settings shows your login and avatar |
| Restart keeps the session | Quit and restart; the account is restored without a new code |
| Denial | Choose Cancel on GitHub's approval page; Repo Watch reports "declined" |
| Expiry | Wait 15 minutes without entering the code; Repo Watch reports "expired" |
| Renewal | Leave Repo Watch running for more than 8 hours, or restart after 8 hours; it stays signed in |
| Revocation | Revoke the app at the URL above, then restart; the widget shows *Reconnect required* and stops |
| Sign-out | Sign out; the `RepoWatch:github/…` entry disappears from Windows Credential Manager |
