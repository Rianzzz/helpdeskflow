# HelpDeskFlow: front-end

React 19 + TypeScript + Vite, Tailwind CSS, React Router e TanStack Query.

```
npm install
npm run dev      # http://localhost:5173 (repassa /api para o gateway em http://localhost:5000)
npm test         # Vitest + Testing Library
npm run e2e      # Playwright (precisa da plataforma em http://localhost:3000, veja abaixo)
npm run lint     # oxlint
npm run build    # checagem de tipos (tsc) + build de produção em dist/
```

O gateway precisa estar no ar (veja o README da raiz). Para apontar para outro gateway: `VITE_GATEWAY_URL=http://host:porta npm run dev`.

## Organização

```
src/
  lib/          api.ts (cliente HTTP + tokens), auth.tsx, queries.ts (dados), tipos, formatação, validação
  components/   ui.tsx (botões, campos, modal, avisos), Layout.tsx (menu), guards.tsx (rotas protegidas)
  pages/        uma tela por arquivo
  test/         ajudantes de teste
```

## Testes de navegador (Playwright)

```
# na raiz do repositório: a plataforma completa, com o limite de login alto (os testes logam muito do mesmo IP)
AUTH_RATE_LIMIT_PER_MINUTE=1000 docker compose --profile apps up -d --build

cd web
npx playwright install chromium     # uma vez
npm run e2e                         # roda tudo (desktop + celular)
npm run e2e:ui                      # modo interativo, ótimo para depurar
npm run e2e:report                  # abre o último relatório HTML
```

Em `e2e/`: `onboarding`, `auth`, `tickets`, `security`, `accessibility` (axe, WCAG AA) e `mobile`. O ajudante `support/stack.ts`
cria empresas e usuários **pela API** (rápido) para cada teste, que então age **pela interface**. Para outro ambiente:
`E2E_BASE_URL=https://staging.exemplo.com npm run e2e`.

## Decisões que valem saber

- **Mesma origem**: o código só chama caminhos relativos (`/api/...`). Em dev o Vite faz o proxy; em produção o nginx. Sem CORS.
- **Tokens**: access token só em memória; refresh token no `sessionStorage`. A renovação é *single-flight* (veja `lib/api.ts`):
  várias requisições com 401 ao mesmo tempo compartilham UMA renovação, porque o servidor trata o reuso de um refresh token
  antigo como roubo e derruba a sessão.
- **Papéis na interface são conveniência**: quem protege de verdade é o servidor.
- **Conteúdo de usuários sempre como texto** (nunca `dangerouslySetInnerHTML`); o nginx ainda aplica uma CSP restritiva.
