# Sync server API

Jotdash works fully offline. To sync to Plane it talks to a small sync server
over plain HTTP + JSON. The reference server is the **QuickNotes server**,
which mirrors tasks into [Plane](https://plane.so) and transcribes voice notes
with Parakeet. Any server that implements these endpoints works.

The app's **Server address** (e.g. `https://your-server/quicknotes/`) is the
base URL. All paths below are relative to it. JSON uses camelCase. The shapes
are in [`Jotdash/Shared/Contracts.cs`](../Jotdash/Shared/Contracts.cs).

| Method | Path | Body / query | Returns |
|--------|------|--------------|---------|
| GET | `health` | – | `{ ok, planeConfigured, lastPlaneSync, lastError, version }` |
| GET | `api/meta` | – | `MetaDto`: workspaces → projects → states (pickers) + `addresses`: the server's direct NetBird/LAN URLs, remembered as backups |
| GET | `api/sync?since=N` | – | `SyncResponse { version, tasks[] }`: tasks changed after version N, including deleted ones (`deleted: true`) |
| POST | `api/sync` | `SyncRequest { since, workspace, project, refresh, knownTaskIds[] }` | Scoped `SyncResponse`: uncached selected tasks plus changes to cached tasks |
| POST | `api/tasks` | `TaskDto` | The stored `TaskDto` (with the new `version`) |
| PUT | `api/tasks/{taskId}/files/{fileId}?kind=audio\|image\|video&name=...` | Raw file bytes, `Content-Type` set | The stored `FileDto` (404 if the task wasn't pushed first) |
| GET | `api/files/{fileId}` | – | File bytes (supports range requests) |

## Rules the app relies on

Jotdash v1.0.10+ uses `POST api/sync` on every sync. `workspace` is the selected
slug; `project` is a single project ID, or null for the entire workspace.
Selecting a scope sets `refresh: true`, polling only its Plane projects.
Unknown workspaces/projects return 400. `knownTaskIds` lists tasks already on
the phone, including tasks captured there and cached outside the current scope.
The response contains:

- All **uncached, non-deleted tasks in the selected scope**, regardless of
  `since`, including completed tasks. Switching cannot skip older tasks even
  with an advanced global cursor.
- Changes after `since` to **already-cached tasks**, even outside the selected
  scope, so moves, deletions and transcripts stay current.

Unchanged cached tasks and uncached tasks outside the selection are not sent.
The response version remains global. Uploads are never scope-filtered.
Attachments download only when opened. A null workspace downloads no new
tasks (but still updates cached ones).
Deploy the updated QuickNotes server **before** installing v1.0.10+. An older
server will report an error rather than silently download every project.
The legacy `GET api/sync` remains available for older apps; its optional
`workspace`, `project`, `refresh` query parameters still return global changes.

`createdAt` and `updatedAt` are task timestamps. Last read is phone-local
metadata (`LocalTask.LastReadAt`), not part of the API and never a Plane edit.

- **Ids come from the phone** (GUIDs), so retrying a POST/PUT never creates
  duplicates.
- **Push the task before its files.**
- **`createdLocally`:** `true` for tasks made on the phone. For those, the
  phone's unsynced edits win over the server's copy. Tasks that came from
  Plane (`false`) always take the server's version.
- **Deletion:** delete a task by POSTing it with `deleted: true`. The server
  reports deletions made in Plane the same way.
- **Transcripts:** `FileDto.transcriptStatus` (`pending`/`done`/`failed`) and
  `transcript` are filled in by the server. The phone picks them up on its
  next sync.

## Authentication

The app can add one of these to every request (set in **⋮ → Settings**):

- Pangolin short browser share link (`https://<pangolin>/s/<token>`): the app
  posts the token to the link origin's `/api/v1/auth/access-token` endpoint,
  then exchanges the returned session at the main QuickNotes address using
  Pangolin's default `resource_session_request_param` query parameter.
  The resource must match the main HTTPS origin. The resulting cookie is used
  for that connection's API calls; each new connection signs in again.
  No notes or server keys are sent during sign-in, and redirects are not followed.
  With no main server address configured, the app first discovers the resource
  via this endpoint and uses its HTTPS origin plus `/quicknotes/`. Existing
  explicit addresses are never replaced. Discovery uses a separate unauthenticated
  client; the API connection then redeems a fresh request token for its session.
- Legacy Pangolin share link: `P-Access-Token-Id` + `P-Access-Token` headers
  (parsed from `https://<pangolin>/s/<id>.<token>` or entered separately).
  These headers go only to the main address or HTTPS addresses.
- HTTP Basic: username + password.
- Any custom header name + value.
- Optional server key: the `X-QuickNotes-Key` header.
