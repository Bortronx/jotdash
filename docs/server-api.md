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
| POST | `api/tasks` | `TaskDto` | The stored `TaskDto` (with the new `version`) |
| PUT | `api/tasks/{taskId}/files/{fileId}?kind=audio\|image\|video&name=...` | Raw file bytes, `Content-Type` set | The stored `FileDto` (404 if the task wasn't pushed first) |
| GET | `api/files/{fileId}` | – | File bytes (supports range requests) |

## Rules the app relies on

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

- Pangolin share link: `P-Access-Token-Id` + `P-Access-Token` headers (parsed from `https://<pangolin>/s/<id>.<token>`). Pangolin credentials go only to the main address or HTTPS addresses.
- HTTP Basic: username + password.
- Any custom header name + value.
- Optional server key: the `X-QuickNotes-Key` header.
