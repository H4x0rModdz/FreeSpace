# FreeSpace

**FreeSpace combines multiple cloud storage accounts into a single space.**

Connect several Google Drive accounts and S3-compatible buckets (AWS S3, Cloudflare R2,
Backblaze B2, Wasabi, MinIO…) and FreeSpace presents them as one drive: one folder tree,
one combined quota, one place to upload, organize, share and download files.

Under the hood, each file goes to an account with available space, following the policy you
choose. For you it is still just `/Photos/trip.jpg`, without needing to know which account holds it.

> **Status:** under active development. Done: authentication, multi-tenancy, invitations,
> auditing, connecting storage accounts (Google Drive and S3) with quota tracking, the virtual
> file tree (folders, move/rename, trash, search), resumable uploads, and downloads (ranged
> streaming, previews, zip, public links), plus a first version of the desktop app (Avalonia UI). See the [roadmap](docs/ARCHITECTURE.md#phases).

## Why it exists

Free or cheap storage is scattered: a few GB in one account, a cheap bucket at another provider,
a work account with room to spare. Managing that by hand is tedious: you have to remember where
each file lives, watch quotas, and move things around when an account fills up.

FreeSpace solves this as a **self-hosted storage gateway**:

- **Aggregated space:** the sum of your accounts shows up as a single storage.
- **Automatic routing:** uploads go to the account with the most free space, round-robin, or by priority.
- **Virtual folders:** organization lives in FreeSpace, independent of where the bytes are.
  Moving or renaming never touches the provider, and a file can change accounts without changing its path.
- **Multi-tenant:** each workspace (personal, family, team) has its own members, roles and
  storage accounts, isolated from the others.
- **Your data, your infrastructure:** runs on any server with Docker. Provider credentials are
  encrypted, and no file is ever made public unless you ask for it.

## How it works

```
                 ┌──────────────┐
  Web / API ───▶ │  FreeSpace   │  virtual tree, quotas, permissions, auditing
                 └──────┬───────┘
                        │ StorageAllocator picks the destination
          ┌─────────────┼─────────────┐
          ▼             ▼             ▼
   Google Drive #1  Google Drive #2  S3 / R2 / B2
```

- **Virtual node → object → replica:** what you see (folder/file) is separate from where the data
  physically lives, which enables relocation and, later, replication across accounts.
- **Direct uploads:** whenever possible the browser sends bytes straight to the storage
  (Drive resumable sessions, S3 presigned URLs) while the server only coordinates.
- **Range downloads:** streaming and seeking in large videos.

Details in [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Desktop app

The main client is a cross-platform desktop app (Avalonia UI) in `src/FreeSpace.Desktop`:

- sign in or create an account against your server; the session survives restarts (the refresh
  token is encrypted with Windows DPAPI, or kept in a user-only file elsewhere);
- switch workspaces, browse folders with breadcrumbs, search, create, rename, trash;
- **drag files onto the window to upload them**. Bytes go straight from your machine to Google Drive
  or the S3 bucket, with retries and resume, so the server's bandwidth is not involved;
- download files (resumable) or double-click to download and open; copy a public link;
- see every storage account with its quota, connect Google Drive (through your browser) or an S3
  bucket, sync and remove;
- restore or permanently delete from the trash.

```bash
dotnet run --project src/FreeSpace.Desktop
```

The look is frosted glass over an iridescent backdrop with Y2K gel controls (glossy pill buttons,
tube-shaped progress bars, Aqua-style file tiles), set in Michroma and Varela Round. Both fonts are
bundled under the SIL Open Font License (`src/FreeSpace.Desktop/Assets/Fonts`). To review the screens
without running the app, set `FREESPACE_SNAPSHOT_DIR` and run the `Capture_screens` test in
`tests/FreeSpace.Desktop.Tests`: it renders each screen with sample data to PNG.

## Stack

- .NET 10 / ASP.NET Core (controllers) with interactive docs via Scalar
- PostgreSQL 18 + Entity Framework Core
- Short-lived JWT access tokens + refresh tokens with rotation and reuse detection
- Argon2id password hashing, rate limiting, RFC 9457 errors (ProblemDetails)
- Docker Compose
- Avalonia UI 12 + CommunityToolkit.Mvvm for the desktop app
- xUnit v3 (Microsoft Testing Platform) + Testcontainers for integration tests against real
  Postgres and S3, and Avalonia headless tests for the desktop screens

## Running with Docker

```bash
cp .env.example .env   # fill in POSTGRES_PASSWORD, JWT_SIGNING_KEY and ENCRYPTION_KEY
docker compose up -d --build --wait
curl http://localhost:8080/health/ready
```

Database migrations run automatically when the API starts. If port 8080 is taken, change
`API_PORT` in `.env`.

## Development

Requires the .NET 10 SDK and Docker.

```bash
docker compose up -d postgres          # or a local Postgres (user/password: freespace)
dotnet run --project src/FreeSpace.Api # uses appsettings.Development.json; docs at /scalar
dotnet test                            # spins up disposable Postgres and S3 containers
dotnet run --project src/FreeSpace.Desktop  # the desktop app
```

New migration:

```bash
dotnet ef migrations add <Name> -p src/FreeSpace.Infrastructure -s src/FreeSpace.Api -o Persistence/Migrations
```

### Layout

```
src/FreeSpace.Domain          entities and business rules (no external dependencies)
src/FreeSpace.Contracts       API request/response types, shared by the API and the clients
src/FreeSpace.Infrastructure  EF Core/Postgres, migrations, crypto, storage providers
src/FreeSpace.Api             controllers, authentication, rate limiting, ProblemDetails
  Configurations/             service registration and pipeline, one file per concern
  Common/                     BaseController and SecureController
src/FreeSpace.Desktop.Core    desktop logic without UI: API client, transfer engines, view models
src/FreeSpace.Desktop         Avalonia desktop app (views, platform services)
tests/FreeSpace.Tests         unit and integration tests (API, desktop client and transfers)
tests/FreeSpace.Desktop.Tests headless UI tests for the desktop screens
docs/                         architecture and roadmap
```

## API (v1)

The interactive reference (Scalar) lives at `/scalar`. It is on automatically in Development;
in production set `API_DOCS_ENABLED=true`.

Controllers follow a hierarchy:
- **`BaseController`:** shared error format (ProblemDetails + `code`). Used directly only by
  session-less routes (login, register, refresh, OAuth callback).
- **`SecureController`:** requires a valid session and an active tenant, enforces
  `[MinimumRole(...)]`, and exposes `UserId`, `TenantId`, `Role` and permission helpers.
  Everything else derives from it.

Every route requires `Authorization: Bearer <accessToken>` unless marked public.
Errors follow RFC 9457 (`application/problem+json`) and carry a stable `code` field.

| Method | Route | Description |
|---|---|---|
| POST | `/api/v1/auth/register` | Public. Creates the user and a personal workspace (owner) |
| POST | `/api/v1/auth/login` | Public. Optional `tenantId` |
| POST | `/api/v1/auth/refresh` | Public. Rotates the refresh token; reusing an old token revokes the session |
| POST | `/api/v1/auth/logout` | Revokes the current session |
| POST | `/api/v1/auth/switch-tenant` | New access token for another of the user's workspaces |
| GET | `/api/v1/me` | User, active workspace and role |
| GET/POST | `/api/v1/tenants` | List my workspaces / create a workspace |
| GET/PATCH | `/api/v1/tenants/current` | View / rename (admin+) |
| GET | `/api/v1/tenants/current/members` | Members |
| PATCH/DELETE | `/api/v1/tenants/current/members/{userId}` | Change role (admin+) / remove (admin+ or the member themselves) |
| GET/POST/DELETE | `/api/v1/tenants/current/invitations[/{id}]` | Invite links (admin+) |
| POST | `/api/v1/invitations/accept` | Accept an invitation |
| GET | `/api/v1/tenants/current/audit-events` | Audit log (admin+), `?limit=&before=` |
| GET | `/api/v1/storage-accounts[/{id}]` | Storage accounts of the workspace, with quota and status |
| GET | `/api/v1/storage-accounts/summary` | Total, used and free space across active accounts |
| POST | `/api/v1/storage-accounts/google/authorize` | Admin+. Returns the Google consent URL |
| GET | `/api/v1/storage-accounts/google/callback` | Public (Google redirect). Connects the account and returns to the frontend |
| POST | `/api/v1/storage-accounts/s3` | Admin+. Validates endpoint and keys (writes and deletes a probe object) |
| PATCH | `/api/v1/storage-accounts/{id}` | Admin+. Name, priority, enable/disable |
| POST | `/api/v1/storage-accounts/{id}/sync` | Admin+. Refresh quota now |
| DELETE | `/api/v1/storage-accounts/{id}` | Admin+. Remove (for Google, also revokes the granted access). Refused while it still holds files |
| GET | `/api/v1/nodes?parentId=&cursor=&limit=` | Folder contents: folders first, then by name, keyset-paginated |
| GET | `/api/v1/nodes/{id}` | A file or folder plus its path from the root (breadcrumbs) |
| GET | `/api/v1/nodes/search?q=&kind=` | Case-insensitive name search across the tree |
| POST | `/api/v1/nodes/folders` | Member+. Create a folder |
| PATCH | `/api/v1/nodes/{id}` | Member+. Rename |
| POST | `/api/v1/nodes/{id}/move` | Member+. Move into another folder (or the root) |
| POST | `/api/v1/nodes/{id}/trash` | Member+. Move to the trash, including everything inside |
| GET | `/api/v1/trash` | Trash entries |
| POST | `/api/v1/trash/{id}/restore` | Member+. Restore an entry and its contents (renamed on clash) |
| DELETE | `/api/v1/trash/{id}` | Member+. Delete forever; the bytes are purged from the providers in the background |
| DELETE | `/api/v1/trash` | Admin+. Empty the trash |
| GET/PUT | `/api/v1/storage-accounts/routing-policy` | How uploads pick an account: `mostAvailable`, `roundRobin`, `priority` (PUT: admin+) |
| POST | `/api/v1/uploads` | Member+. Start an upload: picks an account, reserves the space, returns chunk size and count |
| GET | `/api/v1/uploads/{id}` | Session state and which chunks the provider already has (for resuming) |
| PUT | `/api/v1/uploads/{id}/chunks/{index}` | Send one chunk through the API (raw body, exact chunk length) |
| POST | `/api/v1/uploads/{id}/chunk-urls` | S3: presigned URLs to send chunks straight to the bucket |
| POST | `/api/v1/uploads/{id}/complete` | Verify all bytes arrived and publish the file node |
| DELETE | `/api/v1/uploads/{id}` | Cancel and release the reserved space |
| GET | `/api/v1/nodes/{id}/content?inline=` | Download a file; honors `Range` (seeking). `inline=true` previews safe types |
| POST | `/api/v1/nodes/{id}/content-link?inline=` | Short-lived link for players/web views (S3: direct presigned URL) |
| POST | `/api/v1/nodes/zip` | Download files and folders as one streamed zip |
| GET | `/api/v1/content/{token}` | Public. Serves a signed content link |
| POST | `/api/v1/nodes/{id}/shares` | Member+. Create a public link (optional `expiresAt`); the token is shown once |
| GET | `/api/v1/shares` | Active public links of the workspace |
| DELETE | `/api/v1/shares/{id}` | Revoke a link (its creator or an admin) |
| GET | `/api/v1/public/shares/{token}[/nodes, /content, /zip]` | Public. What a link exposes: info, folder listing, file content, zip |
| GET | `/health/live`, `/health/ready` | Public |

Roles: `viewer < member < admin < owner`. Admins manage and grant only roles below admin,
and every workspace keeps at least one owner.

## Connecting storage

**Google Drive:** create an OAuth client of type *Web application* in Google Cloud Console,
enable the Drive API, and register `GOOGLE_REDIRECT_URI` as an authorized redirect URI.
FreeSpace requests only the `drive.file` scope, so it sees **only the files it created itself**,
never the rest of your Drive. This scope does not require a Google security review.

Native clients (like the desktop app) pass a `returnUrl` to
`POST /api/v1/storage-accounts/google/authorize`. After consent, the browser is sent there with
`status` and `accountId` appended. Only loopback addresses (`http://127.0.0.1:{port}/...`, RFC 8252)
and registered app schemes (`App:NativeRedirectSchemes`, default `freespace://`) are accepted.

**S3-compatible:** provide endpoint, region, bucket and keys. The connection is saved only if
the keys can write and delete a probe object under the prefix. Endpoints on private networks
and plain HTTP are blocked by default (SSRF protection); for a MinIO on your LAN, enable
`S3_ALLOW_PRIVATE_ENDPOINTS` and `S3_ALLOW_INSECURE_ENDPOINTS`.

## Uploading files

1. `POST /api/v1/uploads` with `fileName`, `sizeBytes`, optional `mimeType` and `parentId`. FreeSpace
   picks an account using the workspace's routing policy and **reserves** the space, so parallel
   uploads never overfill an account. The response has `chunkSize` and `chunkCount`.
2. Send every chunk (`chunkSize` bytes; the last one may be shorter), either:
   - **through the API:** `PUT /api/v1/uploads/{id}/chunks/{index}`, which streams to the provider
     without storing the file on the server; or
   - **directly to the provider**, so the bytes skip the server entirely:
     - Google Drive: `PUT` each chunk, in order, to `directUploadUrl` with a `Content-Range` header.
       For browsers, set `FRONTEND_URL` so Google allows that origin.
     - S3: ask `POST /api/v1/uploads/{id}/chunk-urls` for presigned URLs and `PUT` each chunk to its
       URL, in any order. Browsers need a bucket CORS rule allowing `PUT` from the web app's origin.
3. `GET /api/v1/uploads/{id}` shows which chunks arrived, to resume after an interruption.
4. `POST /api/v1/uploads/{id}/complete` verifies the bytes at the provider and creates the file.
   A name clash in the folder gets a ` (n)` suffix.

Unfinished uploads expire after `Storage:UploadSessionHours` (24 h by default) and release their space.
Each file lives in one account; the largest file is limited by the free space of a single account.

## Downloading and sharing

- `GET /api/v1/nodes/{id}/content` streams the file from its provider and honors a single
  `Range`, so players can seek in large videos without downloading everything.
- Clients that cannot send the bearer token (media players, web views, `<img>`) ask for
  `POST /api/v1/nodes/{id}/content-link`: a signed URL valid for `Storage:ContentLinkMinutes`
  (60 by default). For S3 it points straight at the bucket, so the bytes skip the server.
- `POST /api/v1/nodes/zip` streams a zip of any selection; folders keep their structure.
- Public links (`POST /api/v1/nodes/{id}/shares`) give read access to a file, or to a folder and
  everything inside it, until they expire or are revoked. Trashing the item pauses them.

Uploaded content is never executed by browsers on the API's origin: responses carry
`X-Content-Type-Options: nosniff` and a sandboxing `Content-Security-Policy`, and only images,
video, audio, PDF and plain text are ever rendered inline.

Credentials are encrypted in the database (AES-256-GCM) with `ENCRYPTION_KEY`, which lives
outside the database. **Keep a backup of that key**: without it, every account has to be reconnected.

## Security

- Never commit `.env`. Secrets come in through environment variables.
- The values in `appsettings.Development.json` are for local development only and must not be
  used in production.
- Found a vulnerability? Open an issue without exploit details and ask for a private channel.
