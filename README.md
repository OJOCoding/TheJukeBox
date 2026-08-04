# TheJukeBox

Self-hosted music streaming from your own Google Drive. Sign in with Google, grant
read-only access to your Drive, and TheJukeBox indexes your audio files and streams them
to a browser.

Built with ASP.NET Core on .NET 10 and a dependency-free vanilla-JavaScript frontend, in a
deliberately monochrome "technical blueprint" style.

> **Status: work in progress.** The backend is complete and tested; the frontend has a
> working library and playback. Several features are not built yet — see
> [What is not built yet](#what-is-not-built-yet). Read that section before deploying
> this for anyone but yourself.

## How it works

- **Sign in with Google.** Each user gets their own account and sees only their own music.
  Your Drive refresh token is encrypted at rest and never leaves the server.
- **You control who gets in.** New accounts wait for your approval by default; you approve or
  disable them from an **ACCOUNTS** screen. Both take effect on the next request, not whenever
  a cookie happens to expire. Configurable to fully open or strictly single-user.
- **Read-only access.** TheJukeBox requests `drive.readonly` and nothing more. It cannot
  create, modify, or delete anything in your Drive.
- **Your library is indexed, not copied.** A background sync caches your audio files'
  metadata in a local SQLite database, so browsing and searching are instant and work even
  when Drive is slow. After the first full listing it refreshes from Drive's change feed,
  which is a single request when nothing has moved — that is what makes a 15-minute timer
  affordable rather than abusive.
- **Playlists, a visible queue, and a resume point.** Build playlists and reorder them, see
  and edit the queue, and pick up where you left off — the queue, its position within the
  track, and shuffle and repeat are saved server-side, so closing the tab or moving to your
  phone resumes the same listening. Play counts and a listening history come with it.
- **Metadata comes from your files.** Embedded ID3/Vorbis/MP4 tags supply artist, album,
  track number, duration, and cover art. Files without tags fall back to parsing the file
  name and folder. Tags are read using HTTP range requests — only the few hundred
  kilobytes containing them, not the whole file.
- **Audio is proxied, not downloaded.** Streaming forwards the browser's `Range` header
  straight through to Drive, so seeking is instant and the server holds one small buffer
  regardless of file size.

Supported formats: MP3, FLAC, WAV, OGG, Opus, M4A/AAC, WMA.

## Quick start with Docker

You need a Google Cloud project with the Drive API enabled and an OAuth client. The
walkthrough is in **[docs/SELF-HOSTING.md](docs/SELF-HOSTING.md)** — including one setting
that will otherwise break your instance every seven days.

```bash
git clone https://github.com/OJOCoding/TheJukeBox.git
cd TheJukeBox

cp .env.example .env
# Edit .env: client id, client secret, and the public URL of your instance.

mkdir -p data && sudo chown -R 64198:64198 data

docker compose up -d
```

Put a TLS-terminating reverse proxy in front of it. The session cookie is marked `Secure`
whenever `App__PublicBaseUrl` is `https`, and browsers will not send a `Secure` cookie over
plain HTTP — so serving this over bare HTTP on a LAN works, but over the internet it must
be HTTPS. `docker-compose.yml` has a commented Caddy block that handles TLS in two lines.

## Running locally

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download). Node is optional and
only used to run the frontend tests.

```bash
# Credentials go in user-secrets, never in appsettings.json (which is committed).
dotnet user-secrets set "Authentication:Google:ClientId"     "<your-client-id>"     --project src/TheJukeBox
dotnet user-secrets set "Authentication:Google:ClientSecret" "<your-client-secret>" --project src/TheJukeBox

dotnet run --project src/TheJukeBox --launch-profile https
```

Then open <https://localhost:7077>. Register `https://localhost:7077/api/auth/callback` as
a redirect URI in Cloud Console; the app logs the exact URIs it will use at startup.

Starting without credentials is not a crash — it prints which settings are missing, in both
configuration-key and environment-variable form, and exits with code 78 (`EX_CONFIG`).

For plain HTTP use `--launch-profile http`, which serves <http://localhost:5255> and avoids
the dev-certificate prompt. To trust the dev certificate instead:
`dotnet dev-certs https --trust`.

## Testing

```bash
dotnet test
```

165 tests. This also runs the frontend's JavaScript tests through a bridge, using Node's
built-in test runner — there is no `package.json` and nothing to install. If Node is
absent they are reported as skipped in the test output. To run them directly:

```bash
node --test tests/js/queue.test.mjs
```

## Configuration

Every setting can be supplied as a configuration key or as an environment variable, where
`:` becomes `__`.

| Key | Environment variable | Required | Notes |
|---|---|---|---|
| `Authentication:Google:ClientId` | `Authentication__Google__ClientId` | yes | OAuth client id |
| `Authentication:Google:ClientSecret` | `Authentication__Google__ClientSecret` | yes | OAuth client secret |
| `App:PublicBaseUrl` | `App__PublicBaseUrl` | yes | Absolute URL, no trailing slash |
| `App:TrustForwardedHeadersFromAnyProxy` | `App__TrustForwardedHeadersFromAnyProxy` | no | Only when a proxy is the sole ingress |
| `Storage:DataPath` | `Storage__DataPath` | no | Defaults to `/data` in a container |
| `Storage:AutoMigrate` | `Storage__AutoMigrate` | no | Default `true` |
| `Storage:BackupsToKeep` | `Storage__BackupsToKeep` | no | Default `5`; `0` disables |
| `Sync:AutoSyncEnabled` | `Sync__AutoSyncEnabled` | no | Default `true` |
| `Sync:IntervalMinutes` | `Sync__IntervalMinutes` | no | Default `15` |
| `Auth:Registration` | `Auth__Registration` | no | `AdminApproval` (default), `Open`, or `SingleUser` |

Everything mutable lives in one directory (`/data`): the SQLite database, the Data
Protection key ring, and pre-migration backups. **Back it up as a unit.** Losing the key
ring makes every stored refresh token undecryptable and forces all users to reconnect.

## API

All endpoints require an authenticated session except where noted. Errors are
[RFC 9457](https://www.rfc-editor.org/rfc/rfc9457) problem responses with stable
`urn:jukebox:*` type values.

| Method | Route | Notes |
|---|---|---|
| GET | `/api/health` | Anonymous |
| GET | `/api/session` | Anonymous; always 200. What the frontend boots from |
| GET | `/api/auth/login?returnUrl=` | Anonymous; redirects to Google |
| POST | `/api/auth/logout` | |
| GET | `/api/auth/me` | |
| POST | `/api/auth/drive/disconnect` | Forgets the Drive grant |
| GET | `/api/tracks?q=&sort=&limit=&offset=` | `sort`: `title`, `artist`, `album`, `added`, `duration`, `played`, `recent` |
| GET | `/api/tracks/{id}/stream` | `GET` and `HEAD`; honours `Range` |
| GET | `/api/art/{sha256}/{size}` | `size`: 96 or 640 |
| GET | `/api/sync/status` | |
| POST | `/api/sync` | Incremental. Idempotent; 202 whether or not it queued new work |
| POST | `/api/sync/full` | Discards the change token and relists everything |
| GET | `/api/playlists` | With track counts and total runtime |
| POST | `/api/playlists` | `{ name, description? }` |
| GET | `/api/playlists/{id}` | Items in order, including unavailable ones |
| PATCH | `/api/playlists/{id}` | `{ name?, description? }`; empty description clears it |
| DELETE | `/api/playlists/{id}` | |
| GET | `/api/playlists/{id}/tracks` | Playable track ids in order. What the player queues |
| POST | `/api/playlists/{id}/tracks` | `{ trackIds }`; appends, skipping ids you do not own |
| DELETE | `/api/playlists/{id}/items/{itemId}` | Closes the gap in the ordering |
| PUT | `/api/playlists/{id}/order` | `{ itemIds }`, the complete list; 409 if it no longer matches |
| GET | `/api/playback` | The saved queue, filtered to what is still playable |
| PUT | `/api/playback` | Saves the queue, cursor, offset and mode |
| GET | `/api/history?limit=` | Newest first |
| POST | `/api/history` | `{ trackId, secondsPlayed, completed }` |
| GET | `/api/admin/users` | Administrator only |
| GET | `/api/admin/registration` | Administrator only; the configured registration mode |
| POST | `/api/admin/users/{id}/approve` | Administrator only |
| POST | `/api/admin/users/{id}/disable` | Administrator only; rotates the account's security stamp |

Endpoints marked "administrator only" require an *approved* administrator. An account awaiting
approval is authenticated but receives `403` from everything except `/api/session`,
`/api/auth/me` and `/api/auth/logout` — it can see why it is waiting, and sign out.

In Development an OpenAPI document is at `/openapi/v1.json` with a browsable reference at
`/scalar/v1`.

## What is not built yet

Being explicit, because the previous version of this file described features that did not
exist:

- **Album and artist browsing.** The data is indexed and sortable; the dedicated views are
  not built.
- **Drag-and-drop reordering.** Playlists reorder with move-up/move-down buttons, which work
  by keyboard and touch. Dragging can be added on top without changing the model.
- **Offline playback.** The app shell is cached and installable, so it loads offline, but
  audio itself is not stored for offline listening.
- **Anti-forgery enforcement.** Configured but not enforced; `SameSite=Lax` cookies block
  cross-site POST in the meantime.
- **Invite links.** Registration is controlled by approval, not by invitation: a stranger can
  still create a pending account and consume a row, they just cannot reach anything. An invite
  token would stop the account being created at all.
- **Shared drives, shortcuts, and multi-parent files** are out of scope.

Tag extraction has unit-tested plumbing but has not yet been exercised against a corpus of
real tagged audio files.

## Architecture

```
src/TheJukeBox/
  Features/{Auth,Library,Streaming}/   endpoints and handlers, by feature
  Infrastructure/{Drive,Metadata,Persistence,Security}/
  Domain/                              entities
  Configuration/                       options, validation, path resolution
  Errors/                              problem types and the exception handler
  wwwroot/                             the frontend: no build step, no npm
tests/TheJukeBox.Tests/                xUnit
tests/js/                              frontend tests, Node's built-in runner
```

Minimal APIs throughout; there are no MVC controllers. SQLite via EF Core, migrations
applied at startup after a `VACUUM INTO` backup.

## Security

- Read-only Drive scope, and per-user tokens: one user's session can never reach another
  user's library.
- Refresh tokens encrypted at rest with the Data Protection API.
- No CORS policy at all — the frontend is same-origin, so cross-origin access is never
  needed.
- OAuth uses the framework handler, which supplies `state` and PKCE.
- Requests are denied by default: an endpoint must opt out of authentication explicitly.

Found something? Open an issue. Please do not include tokens or database files.

## Licence

MIT. See [LICENSE](LICENSE).
