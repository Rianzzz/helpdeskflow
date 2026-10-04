# CI/CD

Pipeline do GitHub Actions. Voltar ao [README](../README.pt-BR.md).

`.github/workflows/ci.yml` roda a cada push e pull request, com permissão mínima (`contents: read`):

0. **Front-end**: `npm ci`, lint, testes, build de produção (com checagem de tipos), tipos dos testes de navegador e `npm audit`
1. **Build e testes**: compila em Release, roda os testes unitários e os de integração (Testcontainers; o runner já tem Docker)
2. **Pacotes vulneráveis**: `dotnet list package --vulnerable --include-transitive` e falha se achar algum
3. **Imagens Docker + fumaça + testes de navegador**: constrói as 6 imagens (5 serviços e o front), sobe a plataforma completa com
   `docker compose --profile apps` (segredos efêmeros gerados na hora), roda `scripts/smoke-test.sh` pelo gateway (saga de onboarding,
   login, chamado, notificação) e depois os **testes de navegador do Playwright**; se algo falhar, guarda o relatório com screenshots e traces

4. **Kubernetes (kind)**: sobe a plataforma num cluster real (Pod Security `restricted`), roda o teste de fumaça, `scripts/k8s-verify.sh`
   (pod privilegiado recusado, NetworkPolicy bloqueando o que não é da arquitetura, matar pods sem derrubar a plataforma) e os testes
   de navegador, e valida o overlay de produção com `kubectl apply --dry-run=server`

O mesmo teste de fumaça serve localmente: `./scripts/smoke-test.sh http://localhost:5000 http://localhost:3000` (o segundo argumento, opcional, valida também o nginx: SPA, proxy da API e cabeçalhos).
