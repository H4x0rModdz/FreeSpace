# FreeSpace — Architecture

FreeSpace aggregates multiple storage accounts (Google Drive, S3-compatible) into a single
virtual, multi-tenant space.

## Decisions

| Topic | Decision |
|---|---|
| Runtime | .NET 10, ASP.NET Core controllers, API reference via Scalar |
| Database | PostgreSQL 18 + EF Core 10 (Npgsql), `snake_case` names |
| Deployment | Docker Compose (`postgres` + `api`) |
| IDs | `Guid.CreateVersion7()` (time-ordered, no index fragmentation) |
| Passwords | Argon2id (OWASP parameters: m=19 MiB, t=2, p=1) |
| Sessions | Short-lived JWT access token (15 min) + opaque refresh token with **rotation and reuse detection** |
| Errors | `ProblemDetails` (RFC 9457) with a stable `code` extension |
| Tenancy | Shared database, `tenant_id` column + **global EF query filter** (fail-closed) |
| Background work | `BackgroundService` workers backed by Postgres state (no Redis for now) |
| API | Versioned under `/api/v1`. New frontend, so the contract is free to evolve |

## API layer

- `BaseController` owns the error contract (ProblemDetails + `code`). Only session-less
  routes derive from it directly: register, login, refresh, OAuth callbacks.
- `SecureController` derives from it and guards everything else: valid session, active tenant,
  and `[MinimumRole(...)]` (on the controller or the action; the action wins). It exposes the
  caller (`UserId`, `TenantId`, `Role`) and permission helpers for target-dependent checks.
- `Program.cs` only composes; each concern registers itself from `Configurations/`
  (options, persistence, security, rate limiting, forwarded headers, services, API pipeline).

## Multi-tenancy

- `Tenant` = a workspace. All content (storage accounts, files, shares, API keys, audit log)
  belongs to a tenant.
- `User` is global; `Membership` links user ↔ tenant with a role `Viewer < Member < Admin < Owner`.
- Registration creates the user **and** a personal tenant where they are `Owner`.
- The session stores the active tenant; the access token carries `tid`. Switching tenants
  (`POST /auth/switch-tenant`) issues a new access token and invalidates the session's previous ones.
- Every authenticated request re-validates session + membership against the database (immediate
  revocation on logout/member removal) and injects the current role as a claim. The role inside
  the JWT is never trusted.
- `ITenantOwned` entities get a global filter `tenant_id = <current tenant>`. With no tenant in
  context (background jobs, anonymous requests) the filter returns nothing; cross-tenant access
  requires an explicit `IgnoreQueryFilters()`.
- Invitations are links: single-use token (stored as a hash), 7-day expiry, role ≤ the inviter's role.

## Storage model

What the user sees is separate from where the bytes live:

```
Node (virtual tree: folder/file, per tenant)
  └── StoredObject (logical bytes: size, sha256, mime type)
        └── Replica (physical copy: StorageAccount + provider object id)
StorageAccount (Google Drive / S3, encrypted credentials, quota, status)
```

- **Folders exist only in the database.** At the provider, objects live in a flat layout
  (`FreeSpace/<tenant>/<object-id>`). Move/rename are database-only operations, and a file can be
  relocated between accounts without changing its virtual path.
- **Sibling names are unique** (case- and Unicode-insensitive) among live nodes, enforced by a
  partial unique index that also covers the root (`NULLS NOT DISTINCT`).
- **Trash** = `Node.TrashedAt` + `TrashRootId`: everything trashed together restores together.
  Permanent deletion removes the nodes and queues orphaned objects' replicas; a background
  purger deletes them at the providers (idempotently).
- **Reconciliation** (planned job) compares replicas with the providers and flags divergence.
  The provider is never the source of truth for the tree.

### IStorageProvider

```csharp
interface IStorageProvider
{
    Task<QuotaSnapshot> GetQuotaAsync(StorageAccount account, CancellationToken ct);

    // Uploads: one fixed chunk size per session, same protocol for every provider.
    Task<UploadStart> BeginUploadAsync(StorageAccount account, UploadSpec spec, long chunkSize, CancellationToken ct);
    Task<UploadProgress> UploadChunkAsync(StorageAccount account, ProviderUpload upload, int index, long offset, long length, Stream content, CancellationToken ct);
    Task<IReadOnlyList<PresignedChunk>> PresignChunksAsync(StorageAccount account, ProviderUpload upload, IReadOnlyList<int> indexes, CancellationToken ct);
    Task<UploadProgress> GetUploadProgressAsync(StorageAccount account, ProviderUpload upload, CancellationToken ct);
    Task<CompletedUpload> CompleteUploadAsync(StorageAccount account, ProviderUpload upload, CancellationToken ct);
    Task AbortUploadAsync(StorageAccount account, ProviderUpload upload, CancellationToken ct);

    Task DeleteObjectAsync(StorageAccount account, string providerObjectId, CancellationToken ct);
    Task DisconnectAsync(StorageAccount account, CancellationToken ct);
}
```

Downloads add `OpenReadAsync(account, objectId, range)` (a stream of the object or one byte range)
and `GetDirectDownloadUrlAsync(...)` (S3 presigned GET; null for Drive).

### Downloads and sharing

- `NodeContentResult` streams one replica (healthy accounts first), honoring a single `Range`.
  User bytes are served with `nosniff` and a sandboxing CSP; only images, video, audio, PDF and
  plain text may render inline, so uploaded HTML/SVG can never run script on the API origin.
- **Content links** are stateless: `base64url(nodeId|tenantId|expiry).HMAC`, with the key derived
  (HKDF) from the encryption key. They serve players and web views that cannot send a bearer token,
  and stop working once the file is trashed or deleted. S3 files may instead get a presigned GET.
- **Zip** downloads stream entry by entry from the providers, preserving folder structure, with
  limits on item count and total size. ZipArchive's remaining synchronous header writes are
  buffered so the response is only ever written asynchronously.
- **Shares** are public links (token stored as a hash) to a file or a folder subtree; every
  request re-checks that the target is inside the shared subtree and not trashed.

### Uploads

- A session fixes the destination account, the chunk size (≥ 8 MiB, a multiple of 256 KiB, at
  most 10,000 chunks) and an expiry. The provider handle (Drive session URI, S3 multipart upload
  id) is stored encrypted.
- **Drive:** a resumable session in the app's `FreeSpace` folder; files are named by object id.
  Chunks must arrive in order. The session URI is itself the credential, so clients may send to it
  directly; the server opens it with the web app's `Origin` so browsers pass CORS.
- **S3:** a multipart upload where chunk *n* is part *n+1*. Parts may arrive in any order, through
  the API (streamed via a presigned URL, never buffered) or straight from the client via presigned
  URLs. Completion lists the parts server-side, so clients never need to report ETags.
- Completion re-checks every byte at the provider, then (in one transaction) creates the replica,
  marks the object available, adds the node and turns the reservation into usage.
- Expired or cancelled sessions abort at the provider and release their reservation.

Uploads prefer a **direct data plane** where the client can reach the provider; otherwise the API
streams. Drive downloads go through the proxy (handing out the token would expose the whole
Drive); S3 can use presigned GETs.

### StorageAllocator

Chooses the destination account based on: free space (minus reservations for in-flight uploads),
account status/health (`NeedsReauth` is excluded), Google's daily upload limit (750 GB/day per
account), and the tenant policy (`MostAvailable`, `RoundRobin`, `Priority`). Reservations,
usage and the daily counter change only through single atomic SQL updates, so concurrent uploads
cannot overcommit an account or lose updates.

## Security rules

- Never change sharing permissions at the provider (no making files public to `anyone`).
- No update/backup/restore endpoints in the API.
- Google sign-in only links to an existing user when the Google e-mail is verified **and** the
  local user's e-mail is verified; otherwise it requires a login plus an explicit link.
- Custom S3 endpoints are validated against SSRF (private/loopback/link-local addresses blocked,
  including on the resolved IP at connect time).
- Public tokens (share, invitation, preview) are always stored as hashes only.
- Provider credentials are encrypted (AES-GCM, key kept outside the database).
- Rate limiting on authentication routes; `ForwardedHeaders` only from known proxies.

## Phases

1. **Foundation** ✅ — solution, Docker, Postgres/EF, auth (register, login, rotating refresh,
   logout, tenant switching), tenants, members, invitations, auditing, ProblemDetails, rate
   limiting, health checks, integration tests with Testcontainers.
2. **Storage accounts** ✅ — `IStorageProvider`, Google OAuth with the `drive.file` scope, S3 with
   SSRF validation (also on the resolved IP, against DNS rebinding), AES-GCM credentials bound to
   the account id, background quota sync, `NeedsReauth` status.
3. **Virtual tree** ✅ — `Node`/`StoredObject`/`Replica`, folders, move/rename (with a cycle guard),
   batch trash with restore, trigram search, keyset pagination, asynchronous replica purge at the
   providers.
4. **Uploads** ✅ — resumable chunked sessions (direct to Drive / S3 presigned parts, or streamed
   through the API), allocator with atomic reservations and routing policies, expiry/cleanup,
   CORS for the web app.
5. **Downloads** ✅ — ranged streaming, safe inline previews, signed content links (S3 presigned
   when possible), streamed zip, public shares for files and folders.
6. **API keys** with per-tenant scopes.
7. **Desktop app** — Avalonia UI client: login, storages, browsing, uploads/downloads, later a
   virtual drive (Windows Cloud Files API).
8. **Extras** — WebDAV, optional per-folder replication, hashing/integrity.
