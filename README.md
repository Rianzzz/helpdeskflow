# HelpDeskFlow

SaaS multi-tenant de help desk (chamados de suporte), construído em microsserviços com .NET 9.

## Como rodar

### Opção A: tudo em contêineres (mais simples)

```
cp .env.example .env        # defina JWT_SIGNING_KEY (obrigatória) e, se quiser, as senhas
docker compose --profile apps up -d --build
```

A API fica em **http://localhost:5000** (gateway). Os serviços ficam numa rede Docker privada e **não** são expostos ao host.
As migrations rodam sozinhas na primeira subida (`Database__MigrateOnStartup`). Para acompanhar os traces:
`docker compose --profile observability up -d jaeger` e defina `OTEL_EXPORTER_OTLP_ENDPOINT=http://jaeger:4317` no `.env`.

Imagens: um único `docker/Dockerfile` multi-stage (compila uma vez, uma imagem enxuta por serviço, processo **sem root**,
healthcheck em `/health/live`).

### Opção B: serviços no `dotnet run` (para desenvolver e depurar)

1. Abra o **Docker Desktop** e suba só a infraestrutura (PostgreSQL com um banco por serviço, RabbitMQ):
   ```
   docker compose up -d
   ```
   Se o volume do Postgres já existia antes de uma fase, crie os bancos que faltarem uma vez:
   `docker exec helpdesk-postgres createdb -U helpdesk helpdesk_tenants` (e `helpdesk_tickets`, `helpdesk_identity`,
   `helpdesk_notifications`). Painel do RabbitMQ: http://localhost:15672 (usuário `helpdesk`, senha `helpdesk_dev`, só dev).
2. Rode os serviços (cada um em um terminal; as migrations são aplicadas sozinhas em Development):
   ```
   dotnet run --project src/Services/Identity/Identity.Api           --urls http://localhost:5081
   dotnet run --project src/Services/Tickets/Tickets.Api             --urls http://localhost:5080
   dotnet run --project src/Services/Notifications/Notifications.Api --urls http://localhost:5082
   dotnet run --project src/Services/Tenants/Tenants.Api             --urls http://localhost:5083
   dotnet run --project src/Gateway/Gateway.Api                      --urls http://localhost:5000
   ```
   > Na primeira vez, suba Tickets e Notifications **antes** de gerar eventos: cada serviço cria suas filas ao iniciar.

### Usando a API

Use **sempre o gateway** (`http://localhost:5000`). Teste com os arquivos `*.http` (VS Code com REST Client, ou Visual Studio).
Fluxo: registrar empresa → acompanhar o status até `Active` → login → usar o `accessToken` nos chamados.

> A chave JWT de `appsettings.Development.json` é só para desenvolvimento. Em produção ela vem de `Jwt__SigningKey`
> (variável de ambiente / cofre de segredos); o serviço recusa subir sem uma chave de pelo menos 32 bytes.

## Estrutura

```
src/Gateway/Gateway.Api                API Gateway (YARP): entrada única, segurança de borda
src/BuildingBlocks/HelpDeskFlow.Auth   contrato compartilhado: papéis, claims, validação de JWT
src/BuildingBlocks/HelpDeskFlow.Contracts            eventos de integração + interfaces (sem dependências)
src/BuildingBlocks/HelpDeskFlow.Messaging.RabbitMq   publicador/consumidor sobre RabbitMQ.Client
src/BuildingBlocks/HelpDeskFlow.Messaging.Outbox     Outbox transacional (publicação confiável de eventos)
src/BuildingBlocks/HelpDeskFlow.Observability        logs (Serilog), traces/métricas (OpenTelemetry), correlation id, health checks
tests/HelpDeskFlow.UnitTests                         testes unitários (domínio, serviços de aplicação com fakes)
tests/HelpDeskFlow.IntegrationTests                  testes com PostgreSQL e RabbitMQ reais (Testcontainers)
src/Services/Notifications/            consome eventos e gera notificações (serviço simples: um projeto só)
src/Services/Tenants/                  perfil/plano da empresa e passo central da saga de onboarding
src/Services/Identity/                 cadastro de empresas, login, JWT, refresh tokens, usuários
src/Services/Tickets/                  chamados (isolados por empresa e por usuário)
  <Serviço>.Domain          regras de negócio puras
  <Serviço>.Application     casos de uso + interfaces
  <Serviço>.Infrastructure  EF Core + PostgreSQL, repositórios, migrations
  <Serviço>.Api             endpoints HTTP (Minimal API)
```

Regra de dependência: `Api → Infrastructure → Application → Domain`. O Domain não depende de nada.

## Segurança implementada

- Senhas com PBKDF2 + salt (nunca em texto puro); política de senha (10+ caracteres, letras e números)
- JWT de 15 min com `tenant_id` e `role`; só aceita HS256 (bloqueia `alg: none`); chave validada na inicialização
- Refresh token aleatório, **guardado só como hash**, com **rotação** e **detecção de reuso** (reuso derruba todas as sessões)
- Bloqueio de conta após 5 senhas erradas (15 min); mesma resposta para "usuário inexistente" e "senha errada"
  (e tempo equalizado) para não permitir enumeração de contas
- Rate limiting por IP nas rotas de autenticação
- Isolamento multi-tenant no banco (Global Query Filter) + clientes só veem os próprios chamados
- Autorização por papéis (Admin, Agent, Customer); tenant sempre vindo do token, nunca do corpo da requisição
- Erros inesperados nunca vazam detalhes internos

### Comunicação entre serviços (eventos com RabbitMQ)

```
Identity ──UserRegistered──────────────► Tickets (cópia local de usuários → valida o responsável)
    └──────────────────────────────────► Notifications (cópia local: quem e qual e-mail notificar)
Tickets ──TicketCreated/Assigned/Resolved──► Notifications (grava a notificação do usuário certo)
```

- Um exchange `topic` (`helpdeskflow.events`); cada serviço tem **a sua fila por evento** (`<serviço>.<evento>`)
- **Publisher confirms** (só considera publicado quando o broker confirmou), mensagens persistentes
- **Ack manual** depois de processar, 3 tentativas com espera crescente e então **DLQ** (`*.dlq`) para análise
- **Idempotência**: entregas duplicadas não geram notificações em dobro (tabela de eventos já processados)
- O Tickets não chama o Identity a cada requisição: valida o responsável por uma **cópia local** mantida por eventos
  (consistência eventual: se um usuário acabou de ser criado, pode levar instantes para ser reconhecido)
- Os serviços sobrevivem a falhas: com o Notifications fora, os eventos aguardam na fila; com o RabbitMQ fora, as
  requisições continuam funcionando e os consumidores reconectam sozinhos
- **Outbox transacional** (Identity, Tickets e Tenants): o evento é gravado na tabela `outbox_messages` **na mesma transação**
  do dado; um dispatcher em segundo plano o publica (ordem preservada, `FOR UPDATE SKIP LOCKED`, "pelo menos uma vez").
  Com o broker fora do ar nada se perde: os eventos ficam no banco e saem quando o RabbitMQ volta
- **Ids de evento determinísticos** (ex.: `TicketSlaBreached`, eventos da saga): o mesmo fato gera sempre o mesmo `EventId`,
  então duplicatas, mesmo vindas de instâncias diferentes, são tratadas como um evento só

> MassTransit foi evitado de propósito: a v9 passou a ser comercial. Usamos `RabbitMQ.Client` direto,
> o que também ajuda a entender o que acontece por baixo.

### SLA (job agendado)

Um job em segundo plano (`SlaMonitor`) verifica, a cada minuto, chamados **abertos e sem responsável** além do prazo da
prioridade (padrão: Urgent 15 min, High 1 h, Medium 4 h, Low 24 h; configurável na seção `Sla`). Ao estourar, marca o chamado
(`slaBreachedAt`) e publica `TicketSlaBreached`; o Notifications escala o alerta para os **administradores** da empresa.
O job varre todas as empresas por métodos de repositório explicitamente "do sistema" (que ignoram o filtro de tenant)
e nenhum endpoint HTTP os usa.

### Saga de onboarding de empresa (coreografada, com compensação)

```
1. Identity  : cria a empresa "Provisioning" + admin ──TenantRegistered──►
2. Tenants   : valida o nome (único, não reservado) e cria perfil/plano
                 ├─ ok ───TenantProvisioned────────► 3a. Identity ativa a empresa ──UserRegistered/TenantActivated──► Tickets, Notifications (boas-vindas)
                 └─ não ──TenantProvisioningFailed─► 3b. Identity COMPENSA: empresa "Failed" + remove o admin (libera o e-mail); Notifications explica o motivo
Timeout: se nada acontecer em 10 min, o Identity desiste, compensa e avisa o Tenants (TenantRegistrationExpired) para desfazer perfil tardio.
```

- `POST /api/auth/register-tenant` responde **202** (recebido, em provisionamento); acompanhe em
  `GET /api/auth/tenants/{id}/status` (`Provisioning` → `Active` | `Failed` + motivo) e faça login quando `Active`
- Ninguém da empresa consegue entrar enquanto ela não está `Active`; **a senha nunca viaja em eventos**
- O Tenants guarda o **estado da saga** (`onboarding_states`): garante idempotência e que um aviso de expiração que chegue
  antes do cadastro impeça perfis órfãos
- Conflito entre ativar e expirar ao mesmo tempo é resolvido por concorrência otimista (o status é token de concorrência)

### Observabilidade

- **Logs estruturados** (Serilog): legíveis no console em desenvolvimento, **JSON** em produção. Toda linha leva o nome do
  serviço, `TraceId` e `CorrelationId`; consumidores acrescentam `EventId` e a fila. Uma linha por requisição HTTP
- **Correlation ID**: o gateway cria (ou aceita só se for um GUID válido, contra "forjar" logs) o `X-Correlation-Id`,
  que viaja para os serviços, volta na resposta e aparece em todos os logs da requisição
- **Traces distribuídos** (OpenTelemetry): uma requisição vira uma árvore de spans por gateway → serviços → PostgreSQL →
  RabbitMQ → outros serviços. O Outbox guarda o `traceparent`, então **o trace continua ligado mesmo com o envio assíncrono**
  (uma saga inteira aparece como UM trace). Ative com `OTEL_EXPORTER_OTLP_ENDPOINT`; para visualizar:
  ```
  docker compose --profile observability up -d jaeger     # http://localhost:16686
  ```
  e rode os serviços com `OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317`
- **Métricas** (requisições, runtime .NET) exportadas pelo mesmo canal OTLP
- **Health checks** públicos e sem detalhes sensíveis: `/health/live` (processo de pé) e `/health/ready` (PostgreSQL e RabbitMQ)

### Testes

```
dotnet test tests/HelpDeskFlow.UnitTests          # rápido, sem dependências
dotnet test tests/HelpDeskFlow.IntegrationTests   # precisa do Docker: sobe PostgreSQL e RabbitMQ e os 4 serviços
```

Os testes de integração exercitam a plataforma de verdade (saga de onboarding, isolamento entre empresas e papéis, JWT
adulterado/forjado, bloqueio de conta, reuso de refresh token, idempotência, DLQ, job de SLA).

### Gateway (borda)

- Entrada única: os clientes só falam com o gateway; as rotas ficam em `Gateway.Api/appsettings.json`
- JWT validado já no gateway (e de novo nos serviços: defesa em profundidade); **toda rota exige login por padrão**,
  só as de `/api/auth` são marcadas como anônimas
- **Rate limiting por empresa** (tenant) nas rotas autenticadas e por IP no login/cadastro, com `Retry-After`
- CORS só para origens listadas; cabeçalhos de segurança (CSP, nosniff, X-Frame-Options, no-store);
  sem `Server: Kestrel`; corpo máximo de 1 MB; timeout de 30 s para os serviços
- `X-Correlation-Id` gerado/propagado em cada requisição (base para os logs distribuídos da Fase 6)
- Os serviços aceitam `X-Forwarded-For` só de proxies confiáveis, para o rate limit enxergar o IP real do cliente

> Em produção os serviços internos não devem ser expostos à internet: só o gateway (rede Docker/Kubernetes privada).

## Roadmap

- [x] Fase 1 — serviço Tickets, EF Core, PostgreSQL, isolamento por tenant (Global Query Filter)
- [x] Fase 2 — serviço Identity (registro, login, JWT com claim de tenant, papéis, refresh tokens)
- [x] Fase 3 — API Gateway (YARP): entrada única, validação de JWT, rate limiting por tenant
- [x] Fase 4 — eventos com RabbitMQ (Identity → Tickets/Notifications), serviço Notifications, DLQ, idempotência
- [x] Fase 5 — Outbox, SLA com job agendado, serviço Tenants e saga de onboarding com compensação e timeout
- [ ] Fase 6 — observabilidade (Serilog, OpenTelemetry), testes, CI/CD
