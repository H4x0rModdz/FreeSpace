# FreeSpace

**FreeSpace junta vários armazenamentos em nuvem num único espaço.**

Você conecta várias contas do Google Drive e buckets S3-compatíveis (AWS S3, Cloudflare R2,
Backblaze B2, Wasabi, MinIO…) e o FreeSpace mostra tudo como um só drive: uma árvore de pastas,
uma quota somada, um lugar só para enviar, organizar, compartilhar e baixar arquivos.

Por baixo, cada arquivo vai para a conta com espaço disponível, conforme a política que você
escolher. Para quem usa, continua sendo só `/Fotos/viagem.jpg`, sem precisar saber em qual
conta ele está.

> **Status:** em desenvolvimento ativo. A fundação (autenticação, multi-tenancy, convites,
> auditoria) está pronta; integração com os storages vem nas próximas fases. Veja o
> [roadmap](docs/ARCHITECTURE.md#fases).

## Por que existe

O espaço gratuito ou barato está espalhado: alguns GB numa conta, um bucket barato em outro
provedor, uma conta de trabalho com folga. Gerenciar isso à mão é trabalhoso: é preciso lembrar
onde está cada arquivo, vigiar quotas e mover coisas quando uma conta enche.

O FreeSpace resolve isso como um **gateway de armazenamento self-hosted**:

- **Espaço agregado:** a soma das contas aparece como um único armazenamento.
- **Roteamento automático:** os uploads vão para a conta com mais espaço livre, em rodízio ou por prioridade.
- **Pastas virtuais:** a organização vive no FreeSpace, independente de onde os bytes estão.
  Mover ou renomear não toca no provider, e um arquivo pode trocar de conta sem mudar de caminho.
- **Multi-tenant:** cada espaço de trabalho (pessoal, família, equipe) tem membros, papéis e
  storages próprios, isolados dos demais.
- **Seus dados, sua infraestrutura:** roda em qualquer servidor com Docker. As credenciais dos
  providers ficam criptografadas e nenhum arquivo é tornado público sem você pedir.

## Como funciona

```
                 ┌──────────────┐
  Web / API ───▶ │  FreeSpace   │  árvore virtual, quotas, permissões, auditoria
                 └──────┬───────┘
                        │ StorageAllocator escolhe o destino
          ┌─────────────┼─────────────┐
          ▼             ▼             ▼
   Google Drive #1  Google Drive #2  S3 / R2 / B2
```

- **Nó virtual → objeto → réplica:** o que você vê (pasta/arquivo) é separado de onde o dado
  está fisicamente, o que permite realocação e, no futuro, replicação entre contas.
- **Uploads diretos:** sempre que possível o navegador envia os bytes direto ao storage
  (sessão resumable do Drive, URLs presigned do S3), e o servidor só coordena.
- **Downloads com Range:** permitem streaming e seek em vídeos grandes.

Detalhes em [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Stack

- .NET 10 / ASP.NET Core Minimal APIs
- PostgreSQL 18 + Entity Framework Core
- JWT de acesso curto + refresh token com rotação e detecção de reuso
- Argon2id para senhas, rate limiting, erros no formato RFC 9457 (ProblemDetails)
- Docker Compose
- xUnit + Testcontainers (testes de integração contra Postgres real)

## Rodando com Docker

```bash
cp .env.example .env   # preencha POSTGRES_PASSWORD e JWT_SIGNING_KEY (ex.: openssl rand -base64 48)
docker compose up -d --build --wait
curl http://localhost:8080/health/ready
```

As migrations do banco rodam automaticamente quando a API sobe. Se a porta 8080 estiver
ocupada, troque `API_PORT` no `.env`.

## Desenvolvimento

Requer o .NET SDK 10 e Docker.

```bash
docker compose up -d postgres          # ou um Postgres local (usuário/senha: freespace)
dotnet run --project src/FreeSpace.Api # usa appsettings.Development.json; OpenAPI em /openapi/v1.json
dotnet test                            # sobe um Postgres descartável via Testcontainers
```

Nova migration:

```bash
dotnet ef migrations add <Nome> -p src/FreeSpace.Infrastructure -s src/FreeSpace.Api -o Persistence/Migrations
```

### Estrutura

```
src/FreeSpace.Domain          entidades e regras de negócio (sem dependências externas)
src/FreeSpace.Infrastructure  EF Core/Postgres, migrations, hashing e tokens
src/FreeSpace.Api             endpoints, autenticação, rate limiting, ProblemDetails
tests/FreeSpace.Tests         testes unitários e de integração
docs/                         arquitetura e roadmap
```

## API (v1)

Todas as rotas exigem `Authorization: Bearer <accessToken>`, exceto as marcadas como públicas.
Os erros seguem a RFC 9457 (`application/problem+json`) e trazem um campo `code` estável.

| Método | Rota | Descrição |
|---|---|---|
| POST | `/api/v1/auth/register` | Público. Cria o usuário e um espaço pessoal (owner) |
| POST | `/api/v1/auth/login` | Público. `tenantId` opcional |
| POST | `/api/v1/auth/refresh` | Público. Rotaciona o refresh token; reusar um token antigo revoga a sessão |
| POST | `/api/v1/auth/logout` | Revoga a sessão atual |
| POST | `/api/v1/auth/switch-tenant` | Novo access token para outro espaço do usuário |
| GET | `/api/v1/me` | Usuário, espaço ativo e papel |
| GET/POST | `/api/v1/tenants` | Listar meus espaços / criar espaço |
| GET/PATCH | `/api/v1/tenants/current` | Ver / renomear (admin+) |
| GET | `/api/v1/tenants/current/members` | Membros |
| PATCH/DELETE | `/api/v1/tenants/current/members/{userId}` | Mudar papel (admin+) / remover (admin+ ou o próprio membro) |
| GET/POST/DELETE | `/api/v1/tenants/current/invitations[/{id}]` | Convites por link (admin+) |
| POST | `/api/v1/invitations/accept` | Aceitar convite |
| GET | `/api/v1/tenants/current/audit-events` | Log de auditoria (admin+), `?limit=&before=` |
| GET | `/health/live`, `/health/ready` | Públicos |

Papéis: `viewer < member < admin < owner`. Admins gerenciam e concedem apenas papéis abaixo
de admin, e todo espaço mantém pelo menos um owner.

## Segurança

- Nunca commite o `.env`. Os segredos entram por variáveis de ambiente.
- Os valores em `appsettings.Development.json` servem só para desenvolvimento local e não devem
  ser usados em produção.
- Encontrou uma vulnerabilidade? Abra uma issue sem detalhes de exploração e peça um canal privado.
