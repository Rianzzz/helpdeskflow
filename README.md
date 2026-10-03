# HelpDeskFlow

SaaS multi-tenant de help desk (chamados de suporte), construído em microsserviços com .NET 9.

## Como rodar (Fase 1)

1. Abra o **Docker Desktop** e suba o banco:
   ```
   docker compose up -d
   ```
2. Rode a API (aplica as migrations sozinha em Development):
   ```
   dotnet run --project src/Services/Tickets/Tickets.Api --urls http://localhost:5080
   ```
3. Teste com `src/Services/Tickets/Tickets.Api/Tickets.Api.http` (VS Code com a extensão REST Client, ou Visual Studio).
   OpenAPI em `http://localhost:5080/openapi/v1.json`.

## Estrutura

```
src/Services/Tickets/
  Tickets.Domain          regras de negócio puras (entidade Ticket, estados)
  Tickets.Application     casos de uso + interfaces (sem saber de banco ou HTTP)
  Tickets.Infrastructure  EF Core + PostgreSQL, repositórios, migrations
  Tickets.Api             endpoints HTTP (Minimal API)
```

Regra de dependência: `Api → Infrastructure → Application → Domain`. O Domain não depende de nada.

## Roadmap

- [x] Fase 1 — serviço Tickets, EF Core, PostgreSQL, isolamento por tenant (Global Query Filter)
- [ ] Fase 2 — serviço Identity (registro, login, JWT com claim de tenant, papéis)
- [ ] Fase 3 — API Gateway (YARP): entrada única, validação de JWT, rate limiting por tenant
- [ ] Fase 4 — serviço Tenants + eventos com RabbitMQ/MassTransit (Notifications)
- [ ] Fase 5 — SLA com jobs agendados, Outbox, Saga de onboarding
- [ ] Fase 6 — observabilidade (Serilog, OpenTelemetry), testes, CI/CD
