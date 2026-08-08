# Self-hosting TheJukeBox

Everything you need to get a working instance, and the things that most commonly go wrong.

- [1. Create a Google Cloud project](#1-create-a-google-cloud-project)
- [2. Configure the OAuth consent screen](#2-configure-the-oauth-consent-screen)
- [3. Create OAuth credentials](#3-create-oauth-credentials)
- [4. Deploy](#4-deploy)
- [5. First run](#5-first-run)
- [Hosting this for other people](#hosting-this-for-other-people)
- [Backups](#backups)
- [Troubleshooting](#troubleshooting)
- [Upgrading](#upgrading)
- [Uninstalling](#uninstalling)

---

## 1. Create a Google Cloud project

1. Go to <https://console.cloud.google.com/projectcreate> and create a project.
2. Enable the Drive API: **APIs & Services → Library → Google Drive API → Enable**.
   Without this, sign-in succeeds and every library call then fails.

## 2. Configure the OAuth consent screen

**APIs & Services → OAuth consent screen.**

- **User type: External.** "Internal" only exists for Google Workspace organisations, and
  restricts sign-in to your own domain.
- Fill in an app name and your email.
- Add the scope `https://www.googleapis.com/auth/drive.readonly`. Nothing else is needed:
  TheJukeBox cannot write to your Drive.

### Then set publishing status to "In production"

This is the setting that catches people out, so it gets its own heading.

An external consent screen left in **Testing** issues refresh tokens that **expire after
7 days**. TheJukeBox stores a refresh token so it can sync your library in the background, so
in Testing mode your instance works for a week and then quietly stops, and every user has to
reconnect their Drive. Nothing about the failure points at the consent screen.

On the OAuth consent screen, click **Publish app**.

You do **not** need to complete Google's verification review to use this yourself or with a
handful of people. An unverified app in production shows an "unverified app" warning that
users click through via **Advanced → Go to (app name)**.

> ### The 100-user cap is permanent — read this before testing
>
> An unverified app requesting a restricted scope is limited to **100 users**, and that cap
> applies **over the lifetime of the Cloud project**. It counts every account that has ever
> granted access, it **cannot be reset**, and creating a new OAuth client id or redeploying
> does not clear it.
>
> Practical consequences:
>
> - Do your throwaway testing in a **separate Cloud project** you are willing to burn.
> - Every friend you hand this to consumes one of the 100 permanently.
> - If you are planning a public instance, read
>   [Hosting this for other people](#hosting-this-for-other-people) first, because the cap is
>   not the main obstacle.

## 3. Create OAuth credentials

**APIs & Services → Credentials → Create credentials → OAuth client ID.**

- **Application type: Web application.**
- Under **Authorised redirect URIs**, add exactly one, replacing the host with your own and
  keeping the path exactly as written:

  ```
  https://music.example.com/api/auth/callback
  ```

  For local development over HTTPS:

  ```
  https://localhost:7077/api/auth/callback
  ```

  Or over plain HTTP, which Google permits for `localhost`:

  ```
  http://localhost:5255/api/auth/callback
  ```

There is only one. Identity and Drive are requested together in a single consent screen, so a
single callback serves both.

TheJukeBox logs the URI it will actually use at startup, at Information level, so you can copy
it verbatim:

```
info: TheJukeBox[1011]
      Register this one OAuth redirect URI verbatim in Google Cloud Console (APIs & Services >
      Credentials > your OAuth client > Authorised redirect URIs), or sign-in fails with
      Google's redirect_uri_mismatch: https://music.example.com/api/auth/callback
```

Copy the client id and client secret.

### Where the credentials go

`.env` at the repository root is the file to edit, whichever way you run this. `docker
compose` reads it, and a local `dotnet run` reads it too — in Development only, at the
*lowest* precedence, so anything set more deliberately still wins:

| How you run it | What supplies configuration | Precedence, lowest first |
|---|---|---|
| `docker compose` | `.env` | `.env` |
| `dotnet run` | `.env`, user-secrets, environment variables, launch profile | `.env` → `appsettings` → user-secrets → environment → launch profile |

That ordering is deliberate. `APP_PUBLIC_BASE_URL` in `.env` is the public URL of a
*deployment*, which is exactly the wrong value for a local run — so the launch profile pins
`App:PublicBaseUrl` to localhost while `.env` still supplies the credentials.

`.env` is only read in Development. A production deployment gets its configuration from the
environment, and silently reading a file from disk there would let a stale checked-out `.env`
override what an operator deliberately set.

To keep credentials out of the repository directory entirely, use user-secrets instead — run
these from `src/TheJukeBox`, substituting *your* values:

```bash
dotnet user-secrets set "Authentication:Google:ClientId" "YOUR_ID.apps.googleusercontent.com"
```

```bash
dotnet user-secrets set "Authentication:Google:ClientSecret" "YOUR_SECRET"
```

Environment variables work for either way of running and override both:
`Authentication__Google__ClientId` and `Authentication__Google__ClientSecret` — double
underscores map to configuration nesting.

If the sign-in screen says **NOT CONFIGURED**, it also shows a redacted fragment of the client
id it actually read. If that fragment is not yours — in particular if it begins
`000000000000-`, this repository's placeholder — the value was set somewhere this process does
not read from, or `.env` still holds the example values. The same fragment appears in the
startup log at event id 1012.

Configuration is read once, at startup. Restart after changing any of it.

## 4. Deploy

```bash
git clone https://github.com/OJOCoding/TheJukeBox.git
cd TheJukeBox
cp .env.example .env
```

Fill in `.env`:

```ini
JUKEBOX_CLIENT_ID=000000000000-xxxx.apps.googleusercontent.com
JUKEBOX_CLIENT_SECRET=GOCSPX-xxxx
APP_PUBLIC_BASE_URL=https://music.example.com
APP_TRUST_PROXY=true
```

Create the data directory with the right owner. The container runs as uid **64198**:

```bash
mkdir -p data
sudo chown -R 64198:64198 data
```

```bash
docker compose up -d
docker compose logs -f
```

You should see the resolved data path, the redirect URI, and `Now listening on`.

### Put TLS in front

`docker-compose.yml` publishes to `127.0.0.1:8080` on purpose. Terminate TLS in a reverse
proxy. With Caddy that is two lines:

```caddy
music.example.com {
    reverse_proxy jukebox:8080
}
```

Uncomment the `caddy` service in `docker-compose.yml` and point DNS at the host.

**Why TLS is not optional over the internet:** when `APP_PUBLIC_BASE_URL` is `https`, the
session cookie is issued with the `__Host-` prefix and the `Secure` attribute. Browsers refuse
to send a `Secure` cookie over plain HTTP, so sign-in would appear to succeed and then do
nothing. Over `http` the app detects this and steps down to an ordinary cookie name, which is
why a LAN instance without TLS still works.

Set `APP_TRUST_PROXY=true` when the proxy is the only route in, so `X-Forwarded-Proto` and
`X-Forwarded-Host` are honoured. Leave it `false` if the container port is directly reachable
— those headers are trivially forged by whoever can reach the port.

## 5. First run

1. Open your instance. You will see the sign-in screen.
2. **Sign in with Google.** Accept the "unverified app" warning if shown.
3. Grant read-only Drive access.
4. Choose **Scan my Drive**. The first sync lists your audio files, then reads embedded tags
   in a second pass. You do not have to wait for it to finish — a **Start listening** button
   appears as soon as anything is playable.

The first account to sign in becomes the administrator, and is approved automatically —
nobody else could approve it. Everyone after that lands on a "waiting for approval" screen
until you let them in from **ACCOUNTS**, unless you have changed `AUTH_REGISTRATION`. See
[Who is allowed to have an account](#who-is-allowed-to-have-an-account).

Sync repeats every 15 minutes by default. It is cheap after the first pass: Drive's change
feed answers with a single request when nothing has moved. **SYNC** in the top bar runs one
immediately, and offers a full relist when you want to discard the change token and start
over.

---

## Hosting this for other people

A common misreading first: **your users do not supply Google credentials.** You supply one
OAuth client for the whole deployment (the `.env` values). Each visitor clicks "Sign in with
Google", authorises with their *own* Google account, and TheJukeBox indexes *their* Drive.
Refresh tokens are stored per user and encrypted, and one user's session cannot reach another
user's library. Nothing about that needs changing to host for others.

What does constrain you is Google's policy on the `drive.readonly` scope, which is
**restricted**:

| Scale | What Google requires |
|---|---|
| Just you, or a few people | Nothing. Publish unverified, users click through the warning. Counts against the permanent 100-user cap. |
| A public instance | **Verification, plus a third-party security assessment (CASA)** — because this app reads Drive data *through a server*. The assessment must be repeated **every 12 months**. |

That second row is the real obstacle, not the user cap. CASA is a paid annual engagement with
an approved assessor, and it applies specifically because the server touches the data — an app
that only ever read Drive from the browser would not need it.

### If you want a genuinely public instance

Switch from `drive.readonly` to **`drive.file`**, which is *not* a restricted scope and needs
neither verification-for-scale nor CASA. The trade-off is real: `drive.file` only grants
access to files the user explicitly picks, so you add the Google Picker and users choose which
folders to expose, instead of the app discovering their whole library.

That is a genuine product decision, not a workaround. "Pick the folders you want to stream" is
arguably better behaviour than "this app can read every file in your Drive", and it is what
makes public hosting legally and practically possible. The scope lives in one place
(`DriveClientFactory.Scopes`), but the Picker flow and per-file grant tracking are real work
and are not built.

### Who is allowed to have an account

Anyone can *reach* your sign-in page, so who gets *in* is controlled by `Auth:Registration`:

| Mode | Behaviour |
|---|---|
| `AdminApproval` **(default)** | New accounts land in `Pending` and see "waiting for approval". You approve them under **ACCOUNTS** in the top bar. |
| `Open` | Anyone who signs in is active immediately. |
| `SingleUser` | Only your own account can ever be active. Later sign-ins are recorded but can never be approved. |

The first account to sign in is always activated and made administrator, in every mode —
nobody else could approve it, so a pending first account would lock the instance permanently.

The default is deliberately the restrictive one. A stranger reaching a public sign-in page and
being handed a working library is bad on its own, and each such account also permanently
consumes one of your Cloud project's 100 unverified-app user slots.

Set it in `.env`, or as a configuration key or environment variable:

```ini
AUTH_REGISTRATION=AdminApproval
```

A pending account signs in successfully but cannot reach anything: every library, streaming
and sync endpoint answers `403`. It can only see the waiting screen and sign out. Google still
has their name and email address at that point, and so does your database — an account row is
created so that you have something to approve.

**Approving and disabling take effect immediately.** The session cookie is revalidated against
the database on every request, so disabling someone ends their live sessions rather than
waiting up to fourteen days for the cookie to expire. Disabling also rotates their security
stamp, which invalidates every cookie they hold. You cannot disable the account you are signed
in with; ask another administrator, since doing it yourself would lock you out with no way
back.

`SingleUser` is the mode to pick if you are exposing an instance publicly but only want it for
yourself. It is closer to safe-by-default than a reverse-proxy password, and it tells the
person plainly that they will not be approved rather than leaving them waiting.

### Filling in what your files do not carry

Optional, off by default. Set `METADATA_ONLINE=true` and a contact address, and a third sync
pass matches tracks against [MusicBrainz](https://musicbrainz.org) and pulls artwork from the
[Cover Art Archive](https://coverartarchive.org):

```ini
METADATA_ONLINE=true
METADATA_ONLINE_CONTACT=you@example.com
```

Why it is off by default: a lookup sends a track's title and artist to a third party. That is
a small disclosure and a reasonable one to want, but if you host this for other people you
would be making it on their behalf.

The contact address is required, not decorative. MusicBrainz's terms ask for an application
name and a contact in the User-Agent, and they block clients that omit one, so enabling
lookups without it is refused at startup. Requests are rate limited to one per second across
the whole process, as their terms require, which means a pass runs at roughly one track per
second and stops after 200 — the rest are picked up by the next sync.

**It only ever fills blanks.** Tags are what you chose; a match is a guess. A track that
already has an artist keeps it, a track that already has a cover keeps it, and a track whose
metadata came from tags stays labelled that way.

**It would rather find nothing than find the wrong thing.** A candidate is accepted only if
the service scores it at least 90, the title corresponds, the artist corresponds when your
file names one, and — the real test — the duration is within eight seconds. That last rule is
what distinguishes a recording from its covers, live takes and remasters, and it is
deliberately strict: a missing cover reads as "not found yet", while a wrong cover reads as a
broken application and nobody goes looking for the setting that caused it.

In practice this means short clips and covers are correctly left alone. A 68-second guitar
cover of a Led Zeppelin song is not their eight-minute recording, and it does not get their
artwork.

Tracks that find no match are remembered and not retried for a month, so a pass does not spend
its whole rate budget re-asking questions that already came back empty. A pass interrupted
because the service is overloaded records nothing and resumes next time.

If you want to tune it, four more keys exist, all configuration-only:
`Metadata:Online:MaxTracksPerPass` (200), `Metadata:Online:MinimumScore` (90),
`Metadata:Online:DurationToleranceSeconds` (8) and `Metadata:Online:RetryAfterDays` (30).
Lowering the score or widening the tolerance buys coverage with wrong answers.

### Before you expose an instance

You are, in practice, operating a service that reads other people's private files. Their audio
flows through your server, and your database holds tokens that can read their Drive. That
deserves a privacy note to your users and a plan for what happens to that data if you shut the
instance down.

Sources: [Restricted scope verification](https://developers.google.com/identity/protocols/oauth2/production-readiness/restricted-scope-verification),
[Unverified apps](https://support.google.com/cloud/answer/7454865).

## Backups

Everything mutable is in the data directory:

```
data/
  jukebox.db          the library index and user accounts
  jukebox.db-wal      write-ahead log
  jukebox.db-shm      shared memory
  keys/               Data Protection key ring
  backups/            automatic pre-migration snapshots
```

**Back up the whole directory as a unit.** In particular do not back up `jukebox.db` without
`keys/`: the key ring is what decrypts the stored Google refresh tokens, so a database
restored without it leaves every user needing to reconnect their Drive.

Stopping the container first gives the cleanest copy. TheJukeBox also takes a consistent
`VACUUM INTO` snapshot into `backups/` before applying any schema migration, keeping the five
most recent — `Storage:BackupsToKeep` changes that, and `0` disables it.

Your music is never copied — it stays in Drive. Losing the data directory costs you your sync
state, not your library.

---

## Troubleshooting

### `redirect_uri_mismatch` from Google

The redirect URI the app sent does not exactly match the one registered in Cloud Console.
Check the startup log for the URI it uses and compare character by character — `http` vs
`https`, a trailing slash, or a `www.` prefix are each enough.

Behind a reverse proxy the usual cause is `APP_PUBLIC_BASE_URL` not being set, or being set to
the container's internal address rather than the public one.

### Exits immediately with code 78

Intentional. Configuration is missing or invalid, and the message names each key in both
configuration and environment-variable form. Check `.env` is present and that `env_file` is
reaching the container: `docker compose config` shows the resolved values.

### It still says "NOT CONFIGURED" after I set my credentials

The message shows a redacted fragment of the client id the app actually read. If that is not
yours — especially if it begins `000000000000-`, this repository's placeholder — the value was
set somewhere this process does not read from, or `.env` is still the copied example.

Note the precedence: `.env` is the *lowest*-priority source, so a leftover user-secret or a
lingering environment variable will beat it. `dotnet user-secrets list --project
src/TheJukeBox` shows what is set. See [Where the credentials go](#where-the-credentials-go).

Restart after changing any of it: configuration is read at startup.

### "The data directory is not writable"

Bind-mount ownership. The container runs as a non-root user:

```bash
sudo chown -R 64198:64198 data
```

### Sign-in appears to work but I am still signed out

Almost always the `Secure` cookie problem described above: `APP_PUBLIC_BASE_URL` says `https`
but the app is being reached over plain HTTP, or the proxy is not forwarding
`X-Forwarded-Proto`. Confirm your proxy sets it, and that `APP_TRUST_PROXY` is `true` when the
proxy is the only route in.

### Background sync stopped working after about a week

The consent screen is still in **Testing**. See
[step 2](#then-set-publishing-status-to-in-production). Publish the app, then have each user
reconnect their Drive once.

### My library shows fewer tracks than I have

Sync only indexes files Drive reports as audio. Files in **shared drives**, **shortcuts**, and
files with multiple parents are out of scope in this version. A file with an unusual or
missing MIME type may also be skipped.

### Tracks show file names instead of artist and album

Those files have no embedded tags, so metadata was guessed from the file name and its folder.
Tagging them (MusicBrainz Picard is the usual tool) and re-running sync will pick up the real
values — content changes are detected, so tags are re-read. Renaming a file does not trigger a
re-read; editing it does.

If the files themselves cannot be tagged, see
[Filling in what your files do not carry](#filling-in-what-your-files-do-not-carry).

### Some tracks have no cover art, and online lookup did not find one

Expected for anything that is not a released recording. The duration test rejects covers, live
takes and clips on purpose — see the same section above. Short clips in particular will never
match, because no released recording is that length.

### An album is split into several entries, or a compilation is scattered

Albums are grouped by album artist, falling back to the track artist when there is no album
artist tag. A compilation whose files carry per-track artists and no `ALBUMARTIST` will
therefore appear once per performer. Setting `ALBUMARTIST` (to "Various Artists", say) on
every file of the record and re-syncing collapses it into one album.

The artists list groups by track artist instead, deliberately, so the people on a compilation
still each have a page.

---

## Upgrading

```bash
docker compose pull
docker compose up -d
```

Migrations are applied automatically at startup, after a backup snapshot. To apply them
manually instead, set `Storage__AutoMigrate=false`; the app will start and warn that the
schema is out of date.

---

## Uninstalling

```bash
docker compose down
rm -rf data
```

Then revoke the app's access to your Google account at
<https://myaccount.google.com/permissions>, and delete the OAuth client in Cloud Console.
Nothing in your Drive is changed by any of this.
