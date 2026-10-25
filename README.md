# Jotdash

**Jot it down in a dash.** Jotdash is a tiny Android app for capturing
thoughts the moment you have them. Open it and tap one of three big buttons:

- **Note**: the keyboard is already open, just type.
- **Voice**: recording starts immediately and keeps going with the screen locked.
- **Photo**: the camera opens for a photo or a video.

Every capture becomes a task. Everything is **saved on the phone first**, so
the app always works offline. When it can reach your server it syncs in the
background, even with the screen locked, into [Plane](https://plane.so) tasks.
Voice notes are transcribed for you.

## Features

- Compact home header with a menu on the right, plus **Photo / Note / Voice**
  capture buttons (Note is in the center).
- A scrollable task list: open tasks first, completed tasks below with crossed-out
  titles and previews. Completed tasks stay available to reopen.
- **Workspace** selector in the three-dot menu: the start screen and All tasks
  show only the selected workspace. The selection survives restarts, and all
  three capture buttons create tasks in that workspace.
- **Created / Modified / Last opened** date and time appear only inside an open
  task. Last opened shows the previous visit on this phone; reading is not an edit or upload.
- Project, status and an accessible **checkmark** completion button fit on one
  row; long project/status names are shortened with an ellipsis. Completed tasks
  show a reopen icon in the same spot.
- A full-width **Close** button directly above Delete saves any pending typing
  and returns home without completing or deleting the task.
- New notes and media captures explicitly start as **To do**, regardless of the
  default status in the Plane project.
- Pick the project and status *after* capturing; capturing never waits on that.
- Add more voice notes, photos or videos to any task.
- Two-way sync. Tasks created or deleted in Plane show up on (or vanish from) the phone.
- Background sync with Android WorkManager, plus a microphone foreground
  service so recordings survive a locked screen.
- Settings for a server behind [Pangolin](https://github.com/fosrl/pangolin):
  share link, Basic Header Auth or a custom header, plus NetBird / Wi-Fi backup addresses.
- Big, thumb-friendly UI in the Microsoft To Do style.

## Install

Download `jotdash.apk` from the
[latest release](https://github.com/Bortronx/jotdash/releases/latest) and open
it on your phone. Allow "Install unknown apps" for your browser. Then:

1. Allow the microphone, camera and notifications.
2. Optional: **⋮ → Settings** → enter your sync server address and sign-in,
   then tap **Test connection** and **Save**.
3. Tap **Allow background sync** if it's shown.

Without a server, Jotdash is a fully local capture app.

### Deployment privacy

The app does not ship a server address, backup address, account or access
credential. Configure your own connection in Settings; it is saved in the
phone's encrypted storage, not in the app package. Automatically discovered
addresses come from your server only after a connection.

Public documentation and tests must use reserved example domains and synthetic
addresses, never a real deployment's URLs, IPs, share links or credentials.
Keep deployment configuration, signing keys and environment files out of Git.
Do not upload filled-in settings screenshots or private connection links.

### Workspaces and task dates

Unsynced tasks created on this phone appear above downloaded tasks, newest
first, in both the start screen's open tasks and All tasks. This works offline
even when server timestamps are ahead of the phone's clock. Returning to a list
starts at the top so new captures are easy to find. Completed tasks remain below
open tasks on the start screen; after upload, normal date ordering resumes.

Open **⋮ → Select workspace / project**, the large button at the top of the
menu. Choose an **entire workspace** row or one of its indented projects.
The current workspace/project is shown below the button and survives restart.
If no workspaces are cached yet, tap **Load workspaces** in the picker.
Cached tasks
appear immediately; Jotdash also syncs and asks the server to refresh that
selection from Plane, downloading task text/status even for completed tasks.
Attachments still download when opened, rather than using phone storage for
every remote recording/video. On a failed/offline refresh, cached tasks remain
available and the menu reports the problem; normal background sync retries later.

New captures use the selected project. For an entire workspace they prefer
the server's default project if it belongs to the workspace,
then its Quick Notes project, then its first project. You can change project/status
after capture as before. An empty workspace gets a Quick Notes project on the server.
Existing tasks are never moved just by changing the menu selection. Before the
first connection, unassigned offline notes belong to the server's default workspace.
If a selected project disappears, its cached scope stays selected and a warning
asks you to choose another scope; the app never silently broadens the download.
Local capture still works. Previously cached tasks stay on the phone when
switching, but only the selected scope appears in lists. Sync also keeps cached
tasks outside the scope up to date, without fetching unrelated uncached tasks.

For v1.0.10+, deploy the updated QuickNotes server before updating the APK;
see [the scoped sync API](docs/server-api.md). Older app versions continue
working with the updated server.

Created/Modified use the saved task timestamps (including Plane timestamps for
imported tasks), displayed in the phone's local date/time format. Last opened is
stored only on this phone, survives restarts/sync, and changes when the task is
opened, not when it merely appears in a list. The screen shows the previous visit,
and records the current visit for next time. Old/unread tasks say
**First opening on this phone**.

### Connecting (any one option is enough)

Jotdash tries the **Server address** first, then each **Backup address**, then
addresses the server reported itself, and uses the first that answers. Easiest first:

1. **NetBird** – with NetBird on, add the server's NetBird IP under *Backup
   addresses* (just the IP) and choose **No sign-in**.
2. **Home Wi-Fi** – tap **Find server on this Wi-Fi / VPN** (or type the LAN IP).
3. **Pangolin Basic Header Auth** – Pangolin → Resources → the resource for
   `sync.example.com` → Authentication → *Basic Header Auth* → Edit → set a
   username/password; enter the same in Jotdash. It is **per resource**: Basic
   auth on Plane's resource doesn't apply here, and your Pangolin account login
   is not Basic auth.
4. **Pangolin share link** – Pangolin → *Share Links* → Create → resource
   `sync.example.com`; choose **Pangolin share link** and paste the link
   (`https://<pangolin>/s/<token>` or the older `/s/<id>.<token>` format).
   Depending on your Pangolin version, create it under **Access Control →
   Shareable Links** instead of the resource's Authentication page.
   Paste the complete link into **Share link (or token)** and leave **Token ID**
   blank. New configurations select **Pangolin share link** by default at the
   top of Settings: paste the link and tap **Save**. When Server address is
   empty, the app discovers the resource from Pangolin and uses its HTTPS
   origin plus `/quicknotes/`. The discovered address is saved after connection.
   Existing connection choices and addresses are preserved.
   Server address, separate token ID, server key and backups are under
   **Advanced connection settings**. Clear an old server address to discover
   a different resource from a new link. Legacy ID/token links still need an
   explicit server address.

Short, single-value links sign in through Pangolin's `/api/v1/auth/access-token`
endpoint and exchange the returned request token for a resource session cookie.
This works without opening a browser, including background sync and attachment
downloads. Each new sync connection signs in again, so no browser cookies need
to be copied or stored. The returned resource must match the main server's HTTPS
origin; backups never redeem the link. This uses Pangolin's default
`resource_session_request_param` setting; deployments with a renamed session
request parameter must use the separate access-token credentials instead.
Older `<id>.<token>` links and separate ID/token fields continue to use
`P-Access-Token-Id` / `P-Access-Token` headers.

The optional **Server key** is the QuickNotes server's key, not a Plane key.
Plane credentials belong on the sync server; the phone does not call Plane
directly. A revoked/expired link or a link for a different resource reports a
sign-in error and leaves captures queued locally.

A bare IP becomes `http://IP:8126/quicknotes/`. After one successful connection
the server's NetBird/LAN addresses are remembered automatically. Pangolin
credentials are sent only to the main address or HTTPS addresses, never to
plain-HTTP backups. Keep share links private; expiry or revocation only stops
sync. Blocked sync never sends notes to a login page and keeps everything queued.

### Troubleshooting a connection

In Settings, run **Test connection**, then **tap the result** – it is copied
to the clipboard ("Copied – paste it anywhere"); long-press selection still
works too. Failed tests/syncs include safe diagnostic text: app/build version,
platform/OS, network, auth mode, which addresses were tried (main / backup N),
HTTP status, response category, redirect presence and timing. They exclude
credentials, URLs, bodies and task/file IDs. The log is in memory and replaced
by the next test/sync. Don't send screenshots of filled credential fields.

Being signed into Pangolin in Chrome/Samsung Internet does not sign Jotdash in.
Browser cookies are isolated from the app's WebView and native background HTTP
client. Do not extract or paste browser session cookies.
#### Use the Pangolin Android app instead of public-resource credentials

For your own phone, a Pangolin **private resource** can avoid public-resource
Header Auth or Link tokens. In Pangolin, define a private resource routed to the
server's reachable address on TCP port `8126`, grant your user or role access,
then sign into the official Pangolin Android app and connect the VPN. Set
Jotdash's server address to
`http://<private-resource-address>:8126/quicknotes/` and choose **No sign-in**.
If the sync server has `QUICKNOTES_API_KEY` configured, enter that key in
Jotdash's **Server key** field as well.

The client does not make your public resource bypass SSO.
The server's port 8126 must be reachable through the private resource, and the
Pangolin Android app must remain connected. Enable its Always-on VPN and
unrestricted battery use if you want it to stay connected in the background.
Official guides:
[Pangolin Android client](https://docs.pangolin.net/manage/clients/platforms/android),
[private resource destinations](https://docs.pangolin.net/manage/resources/private/destinations),
[private resource access](https://docs.pangolin.net/manage/resources/private/authentication).

Voice notes, photos and videos save their attachment and task together on the
phone before navigating to the task. No network or server configuration is
needed. If saving a recording fails, the recording is retained and **Retry save**
is shown. Camera failures are shown on the capture screen rather than silently
doing nothing.

## The sync server

Jotdash talks to a small HTTP/JSON sync server. That server creates the
Plane tasks, uploads attachments and runs speech-to-text. The API is
documented in [docs/server-api.md](docs/server-api.md).

## Why a native app and not a PWA?

A browser PWA can't keep recording with the screen locked (Android Chrome
pauses `MediaRecorder`). It also can't reliably sync in the background (iOS
has no Background Sync API). Jotdash is a **.NET MAUI Blazor Hybrid** app:

- The UI is plain Blazor (Razor + CSS).
- A few small native Android pieces handle recording and background work.

## Project layout

| Path | Role |
|------|------|
| `Jotdash/Components/Pages/` | Screens: Home, TaskPage, Record, Menu, Settings, AllTasks |
| `Jotdash/wwwroot/app.css` | The whole look, one stylesheet |
| `Jotdash/Services/` | `LocalStore` (JSON + files on the phone), `SyncService` (push → upload files → pull), `SettingsService` |
| `Jotdash/Shared/Contracts.cs` | JSON contracts shared with the server |
| `Jotdash/Platforms/Android/` | `Recorder` (foreground service), `BackgroundSync` (WorkManager), `Phone` (camera, permissions, battery) |
| `build-apk.cs` / `.cmd` | Local build → `dist/jotdash.apk` |
| `.github/workflows/android.yml` | CI build; a `v*` tag publishes a GitHub Release |

## Build it yourself

You need the .NET 10 SDK with the MAUI Android workload, plus a JDK 17+ and the
Android SDK (Android Studio provides both).

```powershell
dotnet workload install maui-android
dotnet build Jotdash/Jotdash.csproj -t:InstallAndroidDependencies -f net10.0-android -p:AcceptAndroidSDKLicenses=True
dotnet run build-apk.cs      # → dist/jotdash.apk
```

`build-apk.cs` also picks up a user-local SDK in `%LOCALAPPDATA%\dotnet-maui`
if one is present (no admin rights needed).

Run the offline-storage regression tests with
`dotnet run --project tests/LocalStore.Tests.csproj`. They exercise voice,
photo and video persistence without a server, failed-save retries, and
completed-task ordering. CI runs these before building the APK.
They also cover short-link session sign-in, rejected/mismatched links, cookie
exchange, legacy token headers and private-network fallback without live credentials.

### Signing and releases

Android only installs an update if it's signed with the **same key** as the
installed app. Release builds are signed with the key stored in the GitHub
secrets `JOTDASH_KEYSTORE_B64` (base64 of the keystore, key alias `jotdash`)
and `JOTDASH_KEYSTORE_PASS`.

To sign a local build with that key, set these environment variables before
running `build-apk.cs`:

- `JOTDASH_KEYSTORE` = the keystore path;
- `JOTDASH_KEYSTORE_PASS` = its password.

Without them, the build uses your Android debug key (fine for testing).

To publish a release:

```powershell
git tag v1.1.0; git push origin v1.1.0
```

## iPhone

The Blazor UI and services are cross-platform, but only Android is built today.
An iOS version needs:

- an `AVAudioRecorder` recorder with the `audio` background mode;
- `BGTaskScheduler` for sync;
- a Mac with Xcode to build it.

## License

[MIT](LICENSE)
