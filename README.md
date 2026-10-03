# HelpDeskFlow

SaaS multi-tenant de help desk (chamados de suporte), construído em microsserviços com .NET 9.

## Como rodar

1. Abra o **Docker Desktop** e suba o banco (cria um banco por serviço):
   ```
   docker compose up -d
   ```
   Se o volume do Postgres já existia antes da Fase 2, crie os bancos uma vez:
   `docker exec helpdesk-postgres createdb -U helpdesk helpdesk_tickets` e o mesmo para `helpdesk_identity`.
2. Rode os serviços (cada um em um terminal; as migrations são aplicadas sozinhas em Development):
   ```
   dotnet run --project src/Services/Identity/Identity.Api --urls http://localhost:5081
   dotnet run --project src/Services/Tickets/Tickets.Api   --urls http://localhost:5080
   dotnet run --project src/Gateway/Gateway.Api            --urls http://localhost:5000
   ```
3. Use **sempre o gateway** (`http://localhost:5000`) como porta de entrada. Teste com os arquivos `*.http`
   (VS Code com REST Client, ou Visual Studio). Fluxo: registrar empresa → login → usar o `accessToken` nos chamados.

> A chave JWT de `appsettings.Development.json` é só para desenvolvimento. Em produção use
> `Jwt__SigningKey` via variável de ambiente / cofre de segredos.

## Estrutura

```
src/Gateway/Gateway.Api                API Gateway (YARP): entrada única, segurança de borda
src/BuildingBlocks/HelpDeskFlow.Auth   contrato compartilhado: papéis, claims, validação de JWT
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
- [ ] Fase 4 — serviço Tenants + eventos com RabbitMQ/MassTransit (Notifications)
- [ ] Fase 5 — SLA com jobs agendados, Outbox, Saga de onboarding
- [ ] Fase 6 — observabilidade (Serilog, OpenTelemetry), testes, CI/CD
