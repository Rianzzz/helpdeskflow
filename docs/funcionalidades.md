# Funcionalidades

Conversa nos chamados, limites do plano, tempo real (SignalR) e o front-end. Voltar ao [README](../README.pt-BR.md).

## Conversa nos chamados (comentários e notas internas)

Cada chamado tem uma conversa: **respostas públicas** (visíveis a quem abriu o chamado) e **notas internas** (só a equipe vê). O
cliente e o atendente conversam e cada mensagem aparece **na hora** na tela do outro (o aviso em tempo real recarrega a conversa).

- **Notas internas nunca vazam**, em quatro camadas: o cliente não consegue criar (403); a API só devolve a ele comentários
  públicos; o *filtro global* do EF Core reforça isso no banco (defesa em profundidade); e o aviso de nota interna só vai para a
  equipe (o handler tem uma salvaguarda explícita, e há teste que prova que o cliente não recebe nem o aviso)
- O evento `TicketCommented` **não carrega o texto** do comentário: o conteúdo fica só no Tickets e é lido pela API com a
  autorização de quem pede. O aviso diz apenas que houve resposta
- Quem é avisado: resposta pública da equipe → quem abriu o chamado (e o responsável, se for outra pessoa); cliente escreveu →
  o responsável (ou toda a equipe, se ainda não há); nota interna → só equipe. O autor nunca é avisado do próprio comentário
- Chamado **fechado** não aceita comentários até ser reaberto; limite de 4000 caracteres; texto sempre exibido como TEXTO
  (nada de HTML), quebras de linha preservadas, Ctrl+Enter envia
- Limite conhecido: autor/equipe recém-criados podem demorar instantes para ser reconhecidos (replicação por evento); a tela mostra
  "Usuário" até lá e o aviso, nesse intervalo, só alcança quem o Notifications já conhece

## Limites do plano (SaaS de verdade)

Cada empresa tem um plano com limites; hoje o **Free** comporta **5 usuários** (o administrador conta como um). Quem DEFINE o
plano é o serviço Tenants; quem APLICA é o Identity, que é quem cadastra pessoas:

```
Tenants decide o plano ──TenantProvisioned{ plano, maxUsers }──► Identity guarda o limite e passa a reservar uma vaga a cada usuário
```

- O limite viaja **no evento** (sem o Identity consultar o Tenants a cada cadastro: se o Tenants cair, os cadastros continuam)
- **À prova de corrida**: o contador de vagas da empresa é *token de concorrência* e é salvo na mesma transação do usuário.
  Seis cadastros simultâneos disputando a última vaga deixam passar **exatamente um**; os outros recebem `409` (teste de integração
  contra PostgreSQL real). Violação do limite responde `409` com mensagem para o administrador, nunca `500`
- A interface mostra o uso ("4 de 5 usuários"), bloqueia "Novo usuário" quando o plano lota e explica o motivo, e a tela de
  Empresa tem uma barra de uso (acessível: `role="progressbar"`). Esconder o botão não é a proteção: o servidor recusa de qualquer forma
- A migration preserva os dados existentes: empresas ativas recebem o limite do Free e o contador reflete os usuários reais
- Próximo passo natural: eventos de mudança de plano (`TenantPlanChanged`) e cobrança; o desenho já comporta, porque o limite é um dado
  que o Identity recebe, não uma regra fixa no código

## Notificações em tempo real (SignalR)

O Notifications hospeda um hub (`/hubs/notifications`): assim que uma notificação é gravada, o servidor a **empurra** para o
navegador de quem a recebe (WebSocket, com fallback automático para SSE/long polling). O navegador chega ao hub por
nginx → gateway (YARP, com cluster próprio e prazo de inatividade maior) → Notifications.

```
evento (RabbitMQ) → handler grava a notificação → empurra ao grupo "user:{tenant}:{usuário}" → contador, lista e aviso na tela
```

- **Acelerador, não fonte da verdade**: a lista continua vindo da API REST. Ao (re)conectar, as listas são recarregadas; sem
  conexão, a tela volta a consultar a API a cada 15 s (o indicador do menu mostra "Ao vivo" ou o modo de reserva). Falha ao
  empurrar nunca derruba o tratamento do evento (o dado já está gravado)
- **Autenticação**: WebSocket de navegador não envia `Authorization`, então o JWT vai em `?access_token=`. Por isso: só é
  aceito nas rotas `/hubs` (em qualquer outra é ignorado); é **redigido** nos logs do nginx (`[REDACTED]`, em todas as rotas),
  nos traces do OpenTelemetry e nunca aparece nos logs do gateway e dos serviços; e o servidor **encerra a conexão quando o
  token expira** (`CloseOnAuthenticationExpiration`), com o cliente reconectando já com um token novo
- **Autorização**: o grupo de cada conexão é montado só com as claims do token (ninguém entra no grupo de outra pessoa) e o hub
  não expõe nenhum método chamável pelo cliente; também empurra "lida em outra aba" para o contador acompanhar
- **Limite conhecido**: com várias instâncias do Notifications, falta um *backplane* (ex.: Redis), porque o evento é consumido
  por uma instância que pode não ser a que tem a conexão do usuário
- Corrigido junto: se o primeiro chamado fosse aberto logo após o cadastro, o aviso podia se perder (o `TicketCreated`
  chegava antes de o Notifications conhecer o admin). Agora o handler tenta de novo em vez de descartar

## Front-end (`web/`)

React 19 + TypeScript + Vite, Tailwind CSS, React Router e TanStack Query. Telas: cadastro de empresa (com o acompanhamento
da **saga** em tempo quase real), login, chamados (filtros, busca, criação, detalhe com ações por papel), notificações
(com contador no menu, **ao vivo** via SignalR), usuários (Admin) e dados da empresa. Responsivo e acessível (rótulos, foco,
`<dialog>`, `aria-live`).

- **Mesma origem**: o navegador só fala com `/api` e `/hubs` (Vite em dev, nginx em produção): sem CORS e sem URL de API no código
- **Tokens**: o access token (15 min) fica **só em memória**; o refresh token no `sessionStorage` (por aba). A renovação é
  *single-flight*: várias requisições com 401 compartilham UMA renovação, senão a detecção de reuso do servidor derrubaria a sessão.
  Próximo passo de endurecimento: cookie `httpOnly` via BFF
- **XSS**: o React escapa todo conteúdo (a descrição dos chamados é exibida como texto, nunca como HTML) e o nginx aplica uma
  **CSP restritiva** (`script-src 'self'`), além de `nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy` e `Permissions-Policy`
- **Papéis na tela são só conveniência**: botões escondidos não protegem nada; o servidor valida o papel do token em cada chamada
- Imagem Docker de ~80 MB (build em Node, servida por nginx **sem root**)
