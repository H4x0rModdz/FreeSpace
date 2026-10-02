# FreeSpace

**FreeSpace combines multiple cloud storage accounts into a single space.**

Connect several Google Drive accounts and S3-compatible buckets (AWS S3, Cloudflare R2,
Backblaze B2, Wasabi, MinIO…) and FreeSpace presents them as one drive: one folder tree,
one combined quota, one place to upload, organize, share and download files.

Under the hood, each file goes to an account with available space, following the policy you
choose. For you it is still just `/Photos/trip.jpg`, without needing to know which account holds it.

> **Status:** under active development. Done: authentication, multi-tenancy, invitations,
> auditing, connecting storage accounts (Google Drive and S3) with quota tracking, and the virtual
> file tree (folders, move/rename, trash, search). Uploads and downloads come in the next phases. See the [roadmap](docs/ARCHITECTURE.md#phases).

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

## Stack

- .NET 10 / ASP.NET Core (controllers) with interactive docs via Scalar
- PostgreSQL 18 + Entity Framework Core
- Short-lived JWT access tokens + refresh tokens with rotation and reuse detection
- Argon2id password hashing, rate limiting, RFC 9457 errors (ProblemDetails)
- Docker Compose
- xUnit + Testcontainers (integration tests against real Postgres and S3)

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
```

New migration:

```bash
dotnet ef migrations add <Name> -p src/FreeSpace.Infrastructure -s src/FreeSpace.Api -o Persistence/Migrations
```

### Layout

```
src/FreeSpace.Domain          entities and business rules (no external dependencies)
src/FreeSpace.Infrastructure  EF Core/Postgres, migrations, crypto, storage providers
src/FreeSpace.Api             controllers, authentication, rate limiting, ProblemDetails
  Configurations/             service registration and pipeline, one file per concern
  Common/                     BaseController and SecureController
tests/FreeSpace.Tests         unit and integration tests
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
| GET | `/health/live`, `/health/ready` | Public |

Roles: `viewer < member < admin < owner`. Admins manage and grant only roles below admin,
and every workspace keeps at least one owner.

## Connecting storage

**Google Drive:** create an OAuth client of type *Web application* in Google Cloud Console,
enable the Drive API, and register `GOOGLE_REDIRECT_URI` as an authorized redirect URI.
FreeSpace requests only the `drive.file` scope, so it sees **only the files it created itself**,
never the rest of your Drive. This scope does not require a Google security review.

**S3-compatible:** provide endpoint, region, bucket and keys. The connection is saved only if
the keys can write and delete a probe object under the prefix. Endpoints on private networks
and plain HTTP are blocked by default (SSRF protection); for a MinIO on your LAN, enable
`S3_ALLOW_PRIVATE_ENDPOINTS` and `S3_ALLOW_INSECURE_ENDPOINTS`.

Credentials are encrypted in the database (AES-256-GCM) with `ENCRYPTION_KEY`, which lives
outside the database. **Keep a backup of that key**: without it, every account has to be reconnected.

## Security

- Never commit `.env`. Secrets come in through environment variables.
- The values in `appsettings.Development.json` are for local development only and must not be
  used in production.
- Found a vulnerability? Open an issue without exploit details and ask for a private channel.
