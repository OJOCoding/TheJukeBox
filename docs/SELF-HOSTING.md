# Self-hosting TheJukeBox

Everything you need to get a working instance, and the three things that most commonly go
wrong.

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
7 days**. TheJukeBox stores a refresh token so it can sync your library in the background,
so in Testing mode your instance works for a week and then quietly stops, and every user
has to reconnect their Drive. Nothing about the failure points at the consent screen.

On the OAuth consent screen, click **Publish app**.

You do **not** need to complete Google's verification review. An unverified app in
production shows an "unverified app" warning that users click through via **Advanced → Go
to (app name)**, and is capped at 100 users — fine for a personal or small-group instance.
Verification only matters if you intend to serve strangers at scale.

## 3. Create OAuth credentials

**APIs & Services → Credentials → Create credentials → OAuth client ID.**

- **Application type: Web application.**
- Under **Authorised redirect URIs**, add both of these, replacing the host with your own
  and keeping the paths exactly as written:

  ```
  https://music.example.com/api/auth/callback
  https://music.example.com/api/auth/drive/callback
  ```

  For local development:

  ```
  https://localhost:7077/api/auth/callback
  https://localhost:7077/api/auth/drive/callback
  ```

TheJukeBox logs the two URIs it will actually use at startup, at Information level, so you
can copy them verbatim:

```
info: TheJukeBox[1011]
      OAuth redirect URIs for this instance (register these verbatim in Google Cloud
      Console): sign-in https://music.example.com/api/auth/callback, Drive
      https://music.example.com/api/auth/drive/callback
```

Copy the client ID and client secret.

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

docker compose up -d
docker compose logs -f
```

You should see the resolved data path, the two redirect URIs, and `Now listening on`.

### Put TLS in front

`docker-compose.yml` publishes to `127.0.0.1:8080` on purpose. Terminate TLS in a reverse
proxy. With Caddy that is two lines:

```caddy
music.example.com {
    reverse_proxy jukebox:8080
}
```

Uncomment the `caddy` service in `docker-compose.yml` and point DNS at the host.

**Why TLS is not optional over the internet:** when `App__PublicBaseUrl` is `https`, the
session cookie is issued with the `__Host-` prefix and the `Secure` attribute. Browsers
refuse to send a `Secure` cookie over plain HTTP, so sign-in would appear to succeed and
then do nothing. Over `http` the app detects this and steps down to an ordinary cookie
name, which is why a LAN instance without TLS still works.

## 5. First run

1. Open your instance. You will see the sign-in screen.
2. **Sign in with Google.** Accept the "unverified app" warning if shown.
3. Grant read-only Drive access.
4. Choose **Scan my Drive**. The first sync lists your audio files, then reads embedded
   tags in a second pass. You do not have to wait for it to finish — a **Start listening**
   button appears as soon as anything is playable.

The first account to sign in becomes the administrator. Anyone with a Google account can
currently sign in and get their own library, so if your instance is on the public internet
and you do not want that, keep it behind your proxy's own authentication for now.

---

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

**Back up the whole directory as a unit.** In particular do not back up `jukebox.db`
without `keys/`: the key ring is what decrypts the stored Google refresh tokens, so a
database restored without it leaves every user needing to reconnect their Drive.

Stopping the container first gives the cleanest copy. TheJukeBox also takes a consistent
`VACUUM INTO` snapshot into `backups/` before applying any schema migration, keeping the
five most recent.

Your music is never copied — it stays in Drive. Losing the data directory costs you your
sync state, not your library.

---

## Troubleshooting

### `redirect_uri_mismatch` from Google

The redirect URI the app sent does not exactly match one registered in Cloud Console.
Check the startup log for the two URIs it uses and compare character by character —
`http` vs `https`, a trailing slash, or a `www.` prefix are all enough.

Behind a reverse proxy the usual cause is `App__PublicBaseUrl` not being set, or being set
to the container's internal address rather than the public one.

### Exits immediately with code 78

Intentional. Configuration is missing or invalid, and the message names each key in both
configuration and environment-variable form. Check `.env` is present and `env_file` is
reaching the container: `docker compose config` shows the resolved values.

### "The data directory is not writable"

Bind-mount ownership. The container runs as a non-root user:

```bash
sudo chown -R 64198:64198 data
```

### Sign-in appears to work but I am still signed out

Almost always the `Secure` cookie problem described above: `App__PublicBaseUrl` says
`https` but the app is being reached over plain HTTP, or the proxy is not forwarding
`X-Forwarded-Proto`. Confirm your proxy sets it, and that
`App__TrustForwardedHeadersFromAnyProxy` is `true` when the proxy is the only route in.

### Background sync stopped working after about a week

The consent screen is still in **Testing**. See [step 2](#then-set-publishing-status-to-in-production).
Publish the app, then have each user reconnect their Drive once.

### My library shows fewer tracks than I have

Sync only indexes files Drive reports as audio. Files in **shared drives**, **shortcuts**,
and files with multiple parents are out of scope in this version. A file with an unusual or
missing MIME type may also be skipped.

### Tracks show file names instead of artist and album

Those files have no embedded tags, so metadata was guessed from the file name and its
folder. Tagging them (MusicBrainz Picard is the usual tool) and re-running sync will pick
up the real values — content changes are detected, so tags are re-read.

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
