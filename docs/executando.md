# Executando e usando

Como subir a plataforma (contêineres, `dotnet run` ou Kubernetes) e como usar a API. Voltar ao [README](../README.pt-BR.md).

## Opção A: tudo em contêineres (mais simples)

```
cp .env.example .env        # defina JWT_SIGNING_KEY (obrigatória) e, se quiser, as senhas
docker compose --profile apps up -d --build
```

O **front-end** fica em **http://localhost:3000** (nginx, que repassa `/api` ao gateway) e a API em **http://localhost:5000** (gateway). Os demais serviços ficam numa rede Docker privada e **não** são expostos ao host.
As migrations rodam sozinhas na primeira subida (`Database__MigrateOnStartup`). Para acompanhar os traces:
`docker compose --profile observability up -d jaeger` e defina `OTEL_EXPORTER_OTLP_ENDPOINT=http://jaeger:4317` no `.env`.

Imagens: um único `docker/Dockerfile` multi-stage (compila uma vez, uma imagem enxuta por serviço, processo **sem root**,
healthcheck em `/health/live`).

## Opção B: serviços no `dotnet run` (para desenvolver e depurar)

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

## Front-end em desenvolvimento

Com a plataforma no ar (Opção A ou B), em outro terminal:

```
cd web
npm install
npm run dev          # http://localhost:5173 (o Vite repassa /api para o gateway em localhost:5000)
npm test             # testes (Vitest + Testing Library)
npm run lint && npm run build
```

## Usando a API

Use **sempre o gateway** (`http://localhost:5000`). Teste com os arquivos `*.http` (VS Code com REST Client, ou Visual Studio).
Fluxo: registrar empresa → acompanhar o status até `Active` → login → usar o `accessToken` nos chamados.

> A chave JWT de `appsettings.Development.json` é só para desenvolvimento. Em produção ela vem de `Jwt__SigningKey`
> (variável de ambiente / cofre de segredos); o serviço recusa subir sem uma chave de pelo menos 32 bytes.

## Opção C: Kubernetes (kind)

```bash
./scripts/k8s-kind.sh up      # front http://localhost:8088, API http://localhost:8089
./scripts/k8s-kind.sh down
```

Manifestos, decisões e o exemplo de produção estão em [k8s/README.md](../k8s/README.md).

## Imagens do README

```bash
cd web && npm run screenshots   # cria uma empresa de demonstração e fotografa as telas em docs/images
```n