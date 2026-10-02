# FreeSpace — Arquitetura

FreeSpace agrega várias contas de armazenamento (Google Drive, S3-compatíveis) num único
espaço virtual, multi-tenant.

## Decisões

| Tema | Decisão |
|---|---|
| Runtime | .NET 10, ASP.NET Core Minimal APIs |
| Banco | PostgreSQL 18 + EF Core 10 (Npgsql), nomes em `snake_case` |
| Deploy | Docker Compose (`postgres` + `api`) |
| IDs | `Guid.CreateVersion7()` (ordenáveis por tempo, sem fragmentar índice) |
| Senhas | Argon2id (parâmetros OWASP: m=19 MiB, t=2, p=1) |
| Sessão | JWT de acesso curto (15 min) + refresh token opaco com **rotação e detecção de reuso** |
| Erros | `ProblemDetails` (RFC 9457) com extensão `code` estável |
| Tenancy | Banco compartilhado, coluna `tenant_id` + **query filter global do EF** (fail-closed) |
| Jobs | `BackgroundService` + tabela de jobs no Postgres (sem Redis por enquanto) |
| API | Versionada em `/api/v1`. Frontend novo, contrato livre |

## Multi-tenancy

- `Tenant` = espaço de trabalho. Todo conteúdo (contas de storage, arquivos, shares, API keys,
  auditoria) pertence a um tenant.
- `User` é global; `Membership` liga usuário ↔ tenant com papel `Viewer < Member < Admin < Owner`.
- Registro cria o usuário **e** um tenant pessoal onde ele é `Owner`.
- A sessão guarda o tenant ativo; o access token carrega `tid`. Trocar de tenant
  (`POST /auth/switch-tenant`) emite novo access token e invalida os anteriores daquela sessão.
- A cada request autenticado validamos sessão + membership no banco (revogação imediata
  em logout/remoção de membro) e injetamos o papel atual como claim — o papel no JWT nunca é confiado.
- Entidades `ITenantOwned` recebem filtro global `tenant_id = <tenant atual>`. Sem tenant no
  contexto (jobs, requests anônimos) o filtro não retorna nada; acesso cross-tenant exige
  `IgnoreQueryFilters()` explícito.
- Convites por link: token de uso único (armazenado como hash), expira em 7 dias, papel ≤ papel de quem convida.

## Modelo de armazenamento (fases 2+)

Separação entre o que o usuário vê e onde os bytes estão:

```
Node (árvore virtual: pasta/arquivo, por tenant)
  └── StoredObject (bytes lógicos: tamanho, sha256, mime)
        └── Replica (cópia física: StorageAccount + id no provider)
StorageAccount (Google Drive / S3, credenciais criptografadas, quota, status)
```

- **Pastas existem só no banco.** No provider os objetos ficam numa estrutura plana
  (`FreeSpace/<tenant>/<object-id>`). Mover/renomear é operação só de banco; um arquivo pode
  ser realocado entre contas sem mudar o caminho virtual.
- **Lixeira** = `Node.TrashedAt`. Exclusão definitiva enfileira a remoção das réplicas.
- **Reconciliação** (job) compara réplicas com o provider e marca divergências — o provider
  nunca é a fonte da verdade da árvore.

### IStorageProvider

```csharp
interface IStorageProvider
{
    Task<UploadTarget> BeginUploadAsync(...);     // sessão resumable do Drive / multipart S3
    Task<UploadProgress> UploadChunkAsync(...);   // quando o backend faz proxy
    Task<Stream> OpenReadAsync(replica, range);  // download com Range
    Task DeleteAsync(replica);
    Task<QuotaInfo> GetQuotaAsync(account);
}
```

Uploads preferem **data plane direto**: o browser recebe a session URI resumable do Drive ou
URLs presigned do S3 e envia os bytes direto ao storage; o backend só controla (init/commit).
Downloads do Drive passam por proxy (entregar o token exporia o Drive inteiro); S3 usa presigned GET.

### StorageAllocator

Escolhe a conta de destino considerando: espaço livre (menos reservas de uploads em andamento),
status/saúde da conta (`NeedsReauth` fica fora), limite diário do Google (750 GB/dia/conta),
política do tenant (`most-available`, `round-robin`, `priority`).

## Segurança — regras de projeto

- Nunca alterar permissões de compartilhamento no provider (nada de tornar arquivos públicos `anyone`).
- Nada de endpoints de update/backup/restore pela API.
- Login Google só vincula a usuário existente se o e-mail do Google for verificado **e** o
  usuário local tiver e-mail verificado; caso contrário exige login + vínculo explícito.
- Endpoints S3 customizados validados contra SSRF (bloquear IPs privados/loopback/link-local).
- Tokens públicos (share, convite, preview) sempre armazenados só como hash.
- Credenciais de providers criptografadas (AES-GCM, chave fora do banco).
- Rate limit nas rotas de autenticação; `ForwardedHeaders` só com proxies conhecidos.

## Fases

1. **Fundação** ✅ — solution, Docker, Postgres/EF, auth (registro, login, refresh rotativo,
   logout, troca de tenant), tenants, membros, convites, auditoria, ProblemDetails, rate limit,
   health checks, testes de integração com Testcontainers.
2. **Storage accounts** — `IStorageProvider`, Google OAuth (conectar contas), S3 (com validação
   SSRF), criptografia de credenciais, sync de quota (job), status `NeedsReauth`.
3. **Árvore virtual** — `Node`/`StoredObject`/`Replica`, pastas, mover/renomear, lixeira, busca, paginação.
4. **Uploads** — sessões resumable (Drive direto, S3 multipart presigned), allocator, expiração/limpeza.
5. **Downloads** — streaming com Range, preview, zip em streaming, shares públicos.
6. **API keys** com escopos por tenant.
7. **Extras** — WebDAV, replicação opcional por pasta, hash/integridade, cliente desktop.
