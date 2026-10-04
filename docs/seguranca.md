# Segurança

O que está implementado, em camadas (defesa em profundidade: cada camada assume que a anterior pode falhar).
Voltar ao [README](../README.pt-BR.md).

```
Navegador ─► nginx (CSP, cabeçalhos) ─► Gateway (JWT, rate limit, CORS) ─► Serviço (JWT de novo, papéis, tenant) ─► Banco (filtro de tenant)
                                                  └─ NetworkPolicy + Pod Security no Kubernetes isolam cada pod ─┘
```

## Identidade e sessão

- Senhas com PBKDF2 + salt (nunca em texto puro); política de senha (10+ caracteres, letras e números)
- JWT de 15 min com `tenant_id` e `role`; só aceita HS256 (bloqueia `alg: none`); a chave é validada na inicialização
  (o serviço recusa subir com menos de 32 bytes)
- Refresh token aleatório, **guardado só como hash**, com **rotação** e **detecção de reuso** (reuso derruba todas as sessões)
- Bloqueio de conta após 5 senhas erradas (15 min); mesma resposta para "usuário inexistente" e "senha errada"
  (e tempo equalizado) para não permitir enumeração de contas
- Rate limiting por IP nas rotas de autenticação e por empresa nas demais, com `Retry-After`

## Multi-tenant e autorização

- Isolamento no banco com Global Query Filter do EF Core: uma consulta nunca enxerga outra empresa, mesmo que o código esqueça um `Where`
- Autorização por papéis (Admin, Agent, Customer); o tenant vem **sempre do token**, nunca do corpo da requisição
- Clientes só veem os próprios chamados; **notas internas** só a equipe vê, garantido em quatro camadas
  (veja [Funcionalidades](funcionalidades.md#conversa-nos-chamados-comentários-e-notas-internas))
- Limite de usuários do plano aplicado no servidor e à prova de corrida (esconder o botão na tela não protege nada)

## Borda (nginx e gateway)

- Toda rota do gateway exige login por padrão; só `/api/auth` é anônima
- CORS apenas para origens listadas; CSP restritiva (`script-src 'self'`), `nosniff`, `X-Frame-Options: DENY`,
  `Referrer-Policy`, `Permissions-Policy`; sem `Server: Kestrel`/versão do nginx; corpo máximo de 1 MB; timeout de 30 s
- `X-Correlation-Id` aceito só se for um GUID válido (contra forjar logs)
- IP real do cliente (para o rate limit) lido de `X-Forwarded-For` **só** de proxies confiáveis

## Front-end

- Access token **só em memória**; refresh token em `sessionStorage` (por aba), com renovação *single-flight*
- React escapa todo conteúdo; comentários e descrições são exibidos como texto, nunca como HTML
- O token do SignalR vai em `?access_token=` (WebSocket de navegador não envia `Authorization`), por isso: só é aceito em
  `/hubs`, é **redigido** nos logs do nginx e nos traces, e a conexão é encerrada quando o token expira

## Mensageria e dados

- A senha **nunca** viaja em eventos; o evento de comentário **não carrega o texto** (só o serviço dono lê, com autorização)
- Outbox transacional e consumidores idempotentes: nada se perde nem duplica efeito
- Um banco por serviço; ninguém lê as tabelas de outro serviço

## Contêineres e Kubernetes

- Imagens enxutas, processo **sem root**, sistema de arquivos somente leitura, sem *capabilities*, `seccomp` padrão
- Pod Security `restricted` imposto pelo cluster (um pod privilegiado é recusado) e NetworkPolicy "nega tudo, libera só o necessário":
  o `web` só fala com o gateway, os serviços só com PostgreSQL e RabbitMQ. Ambos são verificados por teste automatizado
  (`scripts/k8s-verify.sh`). Detalhes em [k8s/README.md](../k8s/README.md)
- Nenhum segredo no Git: em Kubernetes o script gera valores aleatórios; no Docker Compose vêm de `.env` (ignorado pelo Git)

## Pipeline

- CI com permissão mínima (`contents: read`), `npm audit` e `dotnet list package --vulnerable --include-transitive` falhando o build
  em vulnerabilidade conhecida

## Erros

- Erros inesperados nunca vazam detalhes internos (resposta genérica; o detalhe fica só no log, com `CorrelationId`)

## Limites conhecidos (documentados de propósito)

- O refresh token ainda fica em `sessionStorage`; o próximo passo é cookie `httpOnly` via BFF
- Sem backplane Redis no SignalR, o Notifications roda com 1 réplica
