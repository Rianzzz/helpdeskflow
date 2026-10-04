# Arquitetura

Estrutura do código, comunicação entre serviços, saga, SLA e gateway. Voltar ao [README](../README.pt-BR.md).

## Estrutura

```
web/                                   front-end React + TypeScript (Vite, Tailwind, TanStack Query)
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

## Comunicação entre serviços (eventos com RabbitMQ)

```
Identity ──UserRegistered──────────────► Tickets (cópia local de usuários → valida o responsável)
    └──────────────────────────────────► Notifications (cópia local: quem e qual e-mail notificar)
Tickets ──TicketCreated/Assigned/Resolved──► Notifications (grava a notificação do usuário certo)
```

- Um exchange `topic` (`helpdeskflow.events`); cada serviço tem **a sua fila por evento** (`<serviço>.<evento>`)
- **Publisher confirms** (só considera publicado quando o broker confirmou), mensagens persistentes
- **Ack manual** depois de processar, 5 tentativas com espera crescente (0,5 s, 1 s, 2 s, 4 s) e então **DLQ** (`*.dlq`) para análise
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

## SLA (job agendado)

Um job em segundo plano (`SlaMonitor`) verifica, a cada minuto, chamados **abertos e sem responsável** além do prazo da
prioridade (padrão: Urgent 15 min, High 1 h, Medium 4 h, Low 24 h; configurável na seção `Sla`). Ao estourar, marca o chamado
(`slaBreachedAt`) e publica `TicketSlaBreached`; o Notifications escala o alerta para os **administradores** da empresa.
O job varre todas as empresas por métodos de repositório explicitamente "do sistema" (que ignoram o filtro de tenant)
e nenhum endpoint HTTP os usa.

## Saga de onboarding de empresa (coreografada, com compensação)

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

## Gateway (borda)

- Entrada única: os clientes só falam com o gateway; as rotas ficam em `Gateway.Api/appsettings.json`
- JWT validado já no gateway (e de novo nos serviços: defesa em profundidade); **toda rota exige login por padrão**,
  só as de `/api/auth` são marcadas como anônimas
- **Rate limiting por empresa** (tenant) nas rotas autenticadas e por IP no login/cadastro, com `Retry-After`
- CORS só para origens listadas; cabeçalhos de segurança (CSP, nosniff, X-Frame-Options, no-store);
  sem `Server: Kestrel`; corpo máximo de 1 MB; timeout de 30 s para os serviços
- `X-Correlation-Id` gerado/propagado em cada requisição
- Os serviços aceitam `X-Forwarded-For` só de proxies confiáveis, para o rate limit enxergar o IP real do cliente

> Em produção os serviços internos não devem ser expostos à internet: só o gateway (rede Docker/Kubernetes privada).
