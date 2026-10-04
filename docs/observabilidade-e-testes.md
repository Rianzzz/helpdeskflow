# Observabilidade e testes

Logs, traces, health checks e a estratégia de testes. Voltar ao [README](../README.pt-BR.md).

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

## Testes

```
dotnet test tests/HelpDeskFlow.UnitTests          # rápido, sem dependências
dotnet test tests/HelpDeskFlow.IntegrationTests   # precisa do Docker: sobe PostgreSQL e RabbitMQ e os 4 serviços
```

Os testes de integração exercitam a plataforma de verdade (saga de onboarding, isolamento entre empresas e papéis, JWT
adulterado/forjado, bloqueio de conta, reuso de refresh token, idempotência, DLQ, job de SLA).

**Front-end** (`web/`):

```
npm test                  # Vitest + Testing Library (99 testes: cliente de API, tempo real, guardas, formulários, plano, conversa)
npm run e2e               # Playwright: navegador de verdade contra a plataforma em contêineres (56 testes)
```

Os testes de navegador (Playwright) abrem o app em `http://localhost:3000` e cobrem: cadastro com acompanhamento da saga
(sucesso, nome duplicado/reservado, senha fraca), login e sessão (redirecionamento de volta, recarregar, sair, refresh token
adulterado, **uma única renovação** quando várias requisições expiram juntas), o ciclo de um chamado entre **três pessoas em
navegadores isolados** (cliente abre → atendente assume e resolve → cliente é avisado e reabre), isolamento entre empresas e
entre clientes, segurança (CSP bloqueando script injetado, HTML de usuário exibido como texto, tokens fora do `localStorage`,
servidor recusando ação proibida), **acessibilidade** (axe, WCAG AA) e celular. Antes, suba a plataforma com o limite de
login alto, porque os testes fazem muitos logins por minuto do mesmo IP:

```
AUTH_RATE_LIMIT_PER_MINUTE=1000 docker compose --profile apps up -d --build      # PowerShell: $env:AUTH_RATE_LIMIT_PER_MINUTE=1000
cd web && npx playwright install chromium && npm run e2e
```

Cada teste cria a própria empresa (nomes aleatórios), então podem rodar em paralelo e repetidas vezes sem limpar nada.
