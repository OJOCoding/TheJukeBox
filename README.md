# TheJukeBox

Your music is already in Google Drive. This turns that folder into a real music player —
searchable library, cover art, playlists, a visible queue, and a resume point that survives
closing the tab.

Nothing is copied. Your files stay in Drive, read-only, and audio is streamed straight
through. Self-hosted: one process, one SQLite file, one directory to back up.

Built with ASP.NET Core on .NET 10 and a dependency-free vanilla-JavaScript frontend, in a
deliberately monochrome "technical blueprint" style.

- [What it does](#what-it-does)
- [Quick start with Docker](#quick-start-with-docker)
- [Running locally](#running-locally)
- [Configuration](#configuration)
- [How it works](#how-it-works)
- [API](#api)
- [Testing](#testing)
- [Layout](#layout)
- [Limitations](#limitations)
- [Security](#security)
- [Licence](#licence)

## What it does

**Reads your Drive, doesn't copy it.** A background sync caches file metadata in SQLite, so
browsing and searching are instant and keep working while Drive is slow. After the first
listing it refreshes from Drive's change feed — a single request when nothing has moved,
which is what makes a 15-minute timer reasonable rather than abusive.

**Takes metadata from the files themselves.** Embedded ID3, Vorbis and MP4 tags supply
artist, album, track number, duration and cover art. Tags are read with HTTP range requests:
the few hundred kilobytes that contain them, never the whole file. Files without usable tags
fall back to parsing the file name and folder.

**Fills the gaps, if you ask it to.** Off by default — matches untagged tracks against
MusicBrainz to recover artwork, artist, album and year. It only ever fills blanks; what your
own tags say is never overwritten, and it would rather find nothing than guess wrong.

**Browse it like a record collection.** A wall of cover art by album, a page per artist, full
text search, and sorting by title, artist, album, date added, duration, play count or last
played. Albums group by album artist so compilations stay whole; artists group by track
artist so everyone on a compilation still has a page.

**Playlists, a queue you can see, and a resume point.** Build playlists from the library or
straight off an album or artist page, reorder them by dragging or with the keyboard, edit the
queue directly, and pick up exactly where you left off — the queue, the position within the
current track, shuffle and repeat are all saved server-side, so closing the tab or moving to
your phone continues the same listening.

**It remembers what you played.** Play counts and a listening log, which is what "most
played" and "recently played" sort by, and what the strip under Now Playing shows.

**Keyboard first, if you want it.** Space, the arrow keys, `S`, `R` and `M` work anywhere
that is not a text field or the track list, and `?` shows the whole card.

**Multi-user, with a door.** Each person signs in with their own Google account and sees only
their own music. New accounts wait for your approval by default; approving or disabling
someone takes effect on their very next request.

**Streams, doesn't download.** The browser's `Range` header is forwarded straight to Drive,
so seeking is instant and the server holds one small buffer regardless of file size.

Supported formats: MP3, WAV, FLAC, OGG, Opus, M4A/AAC, MP4 audio, WMA.

## Quick start with Docker

You need a Google Cloud project with the Drive API enabled and an OAuth client. The
walkthrough is in **[docs/SELF-HOSTING.md](docs/SELF-HOSTING.md)** — including the one
setting that otherwise breaks your instance every seven days.

```bash
git clone https://github.com/OJOCoding/TheJukeBox.git
cd TheJukeBox
cp .env.example .env
```

Fill in `.env` with your client id, client secret and the public URL of your instance, then:

```bash
mkdir -p data && sudo chown -R 64198:64198 data
docker compose up -d
```

The container runs as uid 64198, which is why the data directory needs that owner.

Put a TLS-terminating reverse proxy in front of it. When `APP_PUBLIC_BASE_URL` is `https`,
the session cookie is issued `Secure` with the `__Host-` prefix, and browsers refuse to send
a `Secure` cookie over plain HTTP — so bare HTTP works on a LAN and cannot work over the
internet. `docker-compose.yml` has a commented Caddy block that handles TLS in two lines.

## Running locally

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download). Node is optional and only
used to run the frontend tests.

```bash
dotnet run --project src/TheJukeBox --launch-profile http
```

That serves <http://localhost:5255>. Register `http://localhost:5255/api/auth/callback` as a
redirect URI in Cloud Console — Google permits plain HTTP for `localhost`, and the
application logs the exact URI it will use at startup.

**Where the credentials come from depends on how you start it.** `.env` is a Docker Compose
convention, so `docker compose` reads it; in Development this application reads it too, at
the lowest precedence, so the same file works both ways. User-secrets override it:

```bash
dotnet user-secrets set "Authentication:Google:ClientId" "<your-client-id>" --project src/TheJukeBox
```

Starting without credentials is not a crash. It prints which settings are missing, in both
configuration-key and environment-variable form, and exits with code 78 (`EX_CONFIG`). If the
sign-in screen says **NOT CONFIGURED**, it also shows a redacted fragment of the client id it
actually read — if that fragment is not yours, the value was set somewhere this process does
not read from.

For HTTPS use `--launch-profile https`, which serves <https://localhost:7077>. Trust the
development certificate first with `dotnet dev-certs https --trust`.

## Configuration

Every setting can be given as a configuration key or as an environment variable, where `:`
becomes `__`. Under Docker, `.env` supplies the shorter names in the third column.

| Key | Environment variable | `.env` | Default |
|---|---|---|---|
| `Authentication:Google:ClientId` | `Authentication__Google__ClientId` | `JUKEBOX_CLIENT_ID` | **required** |
| `Authentication:Google:ClientSecret` | `Authentication__Google__ClientSecret` | `JUKEBOX_CLIENT_SECRET` | **required** |
| `App:PublicBaseUrl` | `App__PublicBaseUrl` | `APP_PUBLIC_BASE_URL` | **required**; absolute, no trailing slash |
| `App:TrustForwardedHeadersFromAnyProxy` | `App__TrustForwardedHeadersFromAnyProxy` | `APP_TRUST_PROXY` | `false` |
| `Auth:Registration` | `Auth__Registration` | `AUTH_REGISTRATION` | `AdminApproval` |
| `Storage:DataPath` | `Storage__DataPath` | — | `/data` in a container |
| `Storage:AutoMigrate` | `Storage__AutoMigrate` | — | `true` |
| `Storage:BackupsToKeep` | `Storage__BackupsToKeep` | — | `5`; `0` disables |
| `Sync:AutoSyncEnabled` | `Sync__AutoSyncEnabled` | — | `true` |
| `Sync:IntervalMinutes` | `Sync__IntervalMinutes` | — | `15` |
| `Metadata:Online:Enabled` | `Metadata__Online__Enabled` | `METADATA_ONLINE` | `false` |
| `Metadata:Online:ContactEmail` | `Metadata__Online__ContactEmail` | `METADATA_ONLINE_CONTACT` | required when enabled |
| `Metadata:Online:MaxTracksPerPass` | `Metadata__Online__MaxTracksPerPass` | — | `200` |
| `Metadata:Online:MinimumScore` | `Metadata__Online__MinimumScore` | — | `90` |
| `Metadata:Online:DurationToleranceSeconds` | `Metadata__Online__DurationToleranceSeconds` | — | `8` |
| `Metadata:Online:RetryAfterDays` | `Metadata__Online__RetryAfterDays` | — | `30` |

`Auth:Registration` takes `AdminApproval`, `Open` or `SingleUser`.
`Metadata:Online:ContactEmail` is sent in the User-Agent, as MusicBrainz's terms require.

Everything mutable lives in one directory (`/data`): the SQLite database, the Data Protection
key ring, and pre-migration backups. **Back it up as a unit.** Losing the key ring makes every
stored refresh token undecryptable and forces all users to reconnect their Drive.

`appsettings.json` deliberately declares none of these keys. An empty string is a value as
far as .NET configuration is concerned, so a blank key there would silently override every
other source.

## How it works

### Syncing, in three passes

A sync runs three passes in increasing order of cost, so the library becomes usable long
before it is complete.

1. **Reconcile.** Lists Drive — or reads its change feed — and writes one row per audio file,
   with metadata guessed from the file name and folder. Fast, and enough to browse and play.
2. **Read tags.** Fetches the few hundred kilobytes of each file that hold its tags, using
   range requests, and replaces the guesses with what the file actually says. This is where
   durations and cover art come from.
3. **Look up online.** Only if enabled. Matches whatever is still missing against MusicBrainz
   and the Cover Art Archive, at one request per second.

Each pass records what it has examined and what the file looked like at the time, so nothing
is re-read without reason: renaming a file does not trigger a re-read, editing it does.

### Cover art

Art is content-addressed — stored under a hash of its own bytes. The twelve tracks ripped
from one album carry byte-identical pictures, so an album collapses to a single row with no
album-grouping logic anywhere, and the same artwork is shared between users who own the same
record. Art fetched online lands in the same table for the same reason.

### Matching, when looking up online

Deliberately hard to satisfy. A candidate is accepted only if MusicBrainz scores it at least
90, the title corresponds, the artist corresponds when your file names one, and — the real
test — the duration is within eight seconds. Titles and artists collide across covers, live
takes and remasters; a length is a measurement. A missing cover reads as "not found yet"; a
wrong cover reads as a broken application.

### The frontend

One document, one persistent `<audio>` element, and a client-side router that swaps the
middle of the page — which is what lets playback survive navigation. There is no build step,
no `package.json` and no dependencies: ES modules served as they are written.

## API

All endpoints require an approved, signed-in account except where noted. Errors are
[RFC 9457](https://www.rfc-editor.org/rfc/rfc9457) problem responses with stable
`urn:jukebox:*` type values.

### Session and account

| Method | Route | Notes |
|---|---|---|
| GET | `/api/health` | Anonymous |
| GET | `/api/session` | Anonymous; always 200. What the frontend boots from |
| GET | `/api/auth/login?returnUrl=` | Anonymous; redirects to Google |
| POST | `/api/auth/logout` | Signed in; approval not required |
| GET | `/api/auth/me` | Signed in; approval not required |
| POST | `/api/auth/drive/disconnect` | Forgets the Drive grant |

### Library

| Method | Route | Notes |
|---|---|---|
| GET | `/api/tracks?q=&sort=&limit=&offset=` | `sort`: `title`, `artist`, `album`, `added`, `duration`, `played`, `recent` |
| GET | `/api/tracks/{id}/stream` | `GET` and `HEAD`; honours `Range` |
| GET | `/api/art/{sha256}/{size}` | `size`: `96` or `640` |
| GET | `/api/albums` | Grouped by album artist, with cover art and runtime |
| GET | `/api/albums/{key}` | One album's tracks, in disc and track order |
| GET | `/api/artists` | Grouped by track artist, with album and track counts |
| GET | `/api/artists/{key}` | One artist's albums and every track |
| GET | `/api/sync/status` | |
| POST | `/api/sync` | Incremental. Idempotent; 202 whether or not it queued new work |
| POST | `/api/sync/full` | Discards the change token and relists everything |

### Playlists, queue and history

| Method | Route | Notes |
|---|---|---|
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

### Administration

Approved administrators only.

| Method | Route | Notes |
|---|---|---|
| GET | `/api/admin/users` | |
| GET | `/api/admin/registration` | The configured registration mode |
| POST | `/api/admin/users/{id}/approve` | |
| POST | `/api/admin/users/{id}/disable` | Rotates the account's security stamp |

An account awaiting approval is authenticated but receives `403` from everything except
`/api/session`, `/api/auth/me` and `/api/auth/logout` — enough to see why it is waiting, and
to sign out.

In Development an OpenAPI document is served at `/openapi/v1.json`, with a browsable
reference at `/scalar/v1`.

## Testing

```bash
dotnet test
```

452 tests. This also runs the frontend's JavaScript tests through a bridge, using Node's
built-in test runner — there is no `package.json` and nothing to install. If Node is absent
they are reported as skipped in the test output. To run them directly:

```bash
node --test tests/js/queue.test.mjs
```

## Layout

```
src/TheJukeBox/
  Features/{Admin,Auth,Library,Playlists,Streaming}/   endpoints and handlers, by feature
  Infrastructure/{Drive,Metadata,Persistence,Security}/
  Domain/                                             entities
  Configuration/                                      options, validation, path resolution
  Errors/                                             problem types and the exception handler
  wwwroot/                                            the frontend: no build step, no npm
tests/TheJukeBox.Tests/                               xUnit
tests/js/                                             frontend tests, Node's built-in runner
```

Minimal APIs throughout; there are no MVC controllers. SQLite through EF Core, with
migrations applied at startup after a `VACUUM INTO` backup.

The client-side routes are `/`, `/library`, `/now`, `/albums`, `/albums/:key`, `/artists`,
`/artists/:key`, `/queue`, `/playlists`, `/playlists/:id`, `/sync` and `/admin`.

## Limitations

Deliberate omissions, stated so nobody has to discover them:

- **Offline playback.** The app shell is cached and installable, so it loads offline, but
  audio itself is not stored for offline listening.
- **Reordering a playlist by touch** means the move-up/move-down buttons. Dragging is there
  for a pointer, but HTML5 drag events do not fire from a touchscreen at all, so the buttons
  are the implementation and dragging is an accelerator over the top of it.
- **The volume slider is desktop-only.** Safari makes `audio.volume` read-only on the grounds
  that the hardware buttons are the volume control, so it is hidden entirely on iOS rather
  than shown doing nothing; at phone widths it gives its place in the transport back to the
  seek bar, which the slider had squeezed to 111px. Mute stays everywhere.
- **Shared drives, shortcuts, and multi-parent files** are out of scope.

## Security

- **Read-only Drive scope**, and per-user tokens. One user's session can never reach another
  user's library; a cross-account request answers `404`, not `403`, because confirming that
  an id exists is itself a disclosure.
- **Refresh tokens are encrypted at rest** with the Data Protection API.
- **Approval is the default**, so reaching the sign-in page is not the same as getting in.
  Approving or disabling someone takes effect on their next request, because the session
  cookie is revalidated against the database rather than trusted for its lifetime. Disabling
  also rotates a security stamp, which invalidates every cookie that account holds.
- **Requests are denied by default**: an endpoint must opt out of authentication explicitly,
  and the default policy requires an *approved* account, not merely a signed-in one.
- **Every response carries a content security policy** with `script-src 'self'` and
  `frame-ancestors 'none'`, alongside `nosniff`, `Referrer-Policy: no-referrer` and
  `X-Frame-Options: DENY`. The strict form is affordable because the frontend is one module
  script from this origin with no inline handlers.
- **Nothing renders markup from data.** Every value that reaches the DOM is set as text —
  file names, tags, playlist names, and anything an online lookup returned.
- **Cover art is served with a MIME type sniffed from its own bytes**, never one that a tag
  or a third party claimed, and always with `nosniff`. The hash in the URL is validated
  against `^[0-9a-f]{32,64}$` and the size against a closed set before either reaches the
  database.
- **Art is the one thing not scoped per user**, and that is deliberate. It is stored under a
  hash of its own bytes so that twelve tracks from one album share a row and two people who
  own the same record share it too — which is also what makes an album a single row with no
  grouping logic. The consequence is that any signed-in user could fetch any art blob whose
  SHA-256 they knew; knowing one means already having the metadata it came from, and guessing
  one is not a thing that happens.
- **No request rate limiting.** Registration is approval-gated and Google caps an unverified
  project at 100 users, so every caller is someone you let in; the page sizes, the queue
  length and the history log are all capped instead. Worth revisiting if you ever run this
  `Open`.
- **No CORS policy at all.** The frontend is same-origin, so cross-origin access is never
  needed and is never granted.
- **OAuth uses the framework handler**, which supplies `state` and PKCE.
- **CSRF**: every state-changing request is `POST`, `PUT`, `PATCH` or `DELETE`, and the
  session cookie is `SameSite=Lax`, which browsers do not send on a cross-site request of
  those methods. There are no state-changing `GET` endpoints, which is what makes that
  sufficient.

Found something? Open an issue. Please do not include tokens or database files.

## Licence

MIT. See [LICENSE](LICENSE).
