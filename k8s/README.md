# HelpDeskFlow no Kubernetes

Como rodar a plataforma inteira (6 serviços + PostgreSQL + RabbitMQ) num cluster Kubernetes, e **por que** cada decisão foi tomada.

## Rodando localmente (kind)

[kind](https://kind.sigs.k8s.io/) cria um cluster Kubernetes dentro de um contêiner Docker. É descartável: não suja o seu computador.

```bash
./scripts/k8s-kind.sh up        # cria o cluster, constrói as imagens, gera os segredos e aplica tudo (~3 min na 1ª vez)
./scripts/smoke-test.sh http://localhost:8089 http://localhost:8088   # a aplicação funciona?
./scripts/k8s-verify.sh         # as garantias do cluster valem? (Pod Security, NetworkPolicy, matar pods)
./scripts/k8s-kind.sh status    # pods, serviços e políticas de rede
./scripts/k8s-kind.sh down      # apaga o cluster
```

- Front-end: <http://localhost:8088> · API (gateway): <http://localhost:8089> (só em `127.0.0.1`).
- Testes de navegador: `cd web && E2E_BASE_URL=http://localhost:8088 npm run e2e`.
- Precisa de: Docker, `kind`, `kubectl` (e `openssl` para gerar os segredos; vem com o Git for Windows).

## Estrutura

```
k8s/
├─ base/                    o que roda em QUALQUER cluster (Deployments, Services, ConfigMap, NetworkPolicies)
│  ├─ patches/              endurecimento e initContainer de migração, aplicados a todos os serviços .NET
│  └─ kustomization.yaml
├─ infra/                   PostgreSQL e RabbitMQ SÓ para desenvolvimento/teste
├─ kind/cluster.yaml        o cluster local e as portas expostas
└─ overlays/
   ├─ kind/                 base + infra + NodePorts + imagens locais
   └─ production-example/   base + Ingress/TLS + HPA + PDB + réplicas (exemplo; troque os "SEU-...")
```

**Kustomize** (já vem no `kubectl`) monta o YAML final somando uma *base* com *overlays*, sem copiar arquivo nem usar
template. A base não sabe se está num notebook ou em produção; cada overlay diz só o que muda.

## Decisões e por quê

### Segurança do pod (Pod Security Standards `restricted`)
O namespace é rotulado com o nível mais rígido (`enforce: restricted`). O **cluster recusa** qualquer pod que:
rode como root, tenha privilégios, não descarte as *capabilities* do Linux, não use `seccomp`... Os nossos pods cumprem:

| Configuração | Para que serve |
|---|---|
| `runAsNonRoot` + UID fixo (1654 .NET, 101 nginx) | uma falha na aplicação não vira acesso de root |
| `readOnlyRootFilesystem` + `emptyDir` em `/tmp` | invasor não consegue gravar um programa no disco do contêiner |
| `capabilities.drop: ALL`, `allowPrivilegeEscalation: false` | sem poderes especiais do Linux |
| `seccompProfile: RuntimeDefault` | bloqueia chamadas de sistema perigosas |
| `automountServiceAccountToken: false` | o pod não recebe credencial para falar com a API do Kubernetes (ele não precisa) |

`k8s-verify.sh` prova a regra: tenta criar um pod privilegiado e confirma que o cluster recusa.

### Rede: tudo proibido, exceto o necessário (NetworkPolicy)
`default-deny-all` bloqueia todo tráfego; depois só abrimos o caminho da arquitetura:

```
internet/ingress ──► web ──► gateway ──► identity | tickets | tenants | notifications ──► postgres, rabbitmq
```

Se um serviço for invadido, ele não alcança os outros nem a internet. Os serviços **não falam entre si por HTTP**:
a comunicação é por eventos (RabbitMQ), então nem existe regra para isso. O `k8s-verify.sh` testa conexões reais
(inclusive de um "pod intruso") para provar o bloqueio. *O CNI do cluster precisa aplicar NetworkPolicy
(kindnet, Calico e Cilium aplicam; sem isso as regras seriam ignoradas em silêncio).*

### Migrações do banco: initContainer, não "na subida"
Antes do contêiner da aplicação, um `initContainer` roda `app --migrate-only` (aplica as migrações do EF Core e sai).
Assim, a aplicação nunca atende tráfego com o banco desatualizado, e com várias réplicas subindo juntas o EF Core usa
um *lock* no banco, então só uma migra por vez. No Kubernetes `Database__MigrateOnStartup=false`.

### Probes, atualização sem queda e desligamento limpo
- `startupProbe` dá tempo de iniciar sem o Kubernetes matar o pod; `livenessProbe` (`/health/live`) reinicia pod travado;
  `readinessProbe` (`/health/ready`, checa banco e fila) tira o pod do balanceamento quando ele não está pronto.
- `RollingUpdate` com `maxUnavailable: 0`: o pod novo precisa ficar pronto antes de o antigo sair.
- `preStop: sleep` + `terminationGracePeriodSeconds`: ao desligar, o pod continua atendendo por alguns segundos
  enquanto o Kubernetes o retira dos balanceadores, depois termina as requisições em andamento. Resultado medido:
  225 requisições durante 15 mortes de pods, **0 falhas** (`k8s-verify.sh`).

### Segredos
Nenhum segredo está no Git. `k8s-kind.sh` gera valores aleatórios e cria o `Secret helpdeskflow-secrets`
(`jwt-signing-key`, `postgres-password`, `rabbitmq-password`). Em produção, crie esse mesmo Secret por outro meio:
External Secrets Operator / Sealed Secrets / o cofre do seu provedor. *Um `Secret` do Kubernetes é só base64, não
criptografia; trate o acesso a ele como o acesso à senha.*

### IP real do usuário atrás do Ingress
Os limites de requisição (rate limit) são por IP. Atrás de um Ingress todo tráfego pareceria vir do mesmo IP, então o
nginx (`web`) lê o `X-Forwarded-For` e o gateway/identity confiam nesse cabeçalho **só** se vier da rede do cluster
(`10.0.0.0/8`, ajustável no ConfigMap `web-real-ip` e em `ForwardedHeaders__KnownNetworks`). Confiar em qualquer origem
deixaria um atacante forjar o IP para escapar do limite.

### O que escala e o que não escala (ainda)
| Serviço | Réplicas | Observação |
|---|---|---|
| identity, tickets, tenants, gateway, web | 2+ (HPA no exemplo de produção) | desenhados sem estado local: Outbox com `SKIP LOCKED`, ids de evento determinísticos, limite de usuários com controle de concorrência |
| **notifications** | **1** | o SignalR guarda as conexões na memória do pod. Para 2+ réplicas é preciso um **backplane Redis**. Próximo passo natural. |

### Armadilha que apareceu: o resolver do nginx
O `proxy_pass` do nginx usa uma variável (`set $gateway ...`) para resolver o DNS **a cada requisição** (se o gateway
mudar de IP, o nginx acompanha). Mas o resolver do nginx **ignora a lista `search` do `resolv.conf`**: o nome curto
`gateway` funciona no Docker Compose e falha no Kubernetes (`SERVFAIL`). Por isso o `web.yaml` usa o nome completo
`gateway.helpdeskflow.svc.cluster.local`.

## Indo para produção

1. Use PostgreSQL e RabbitMQ **gerenciados** (ou operadores com backup/HA): não aplique `infra/`.
2. Copie `overlays/production-example`, troque os `SEU-...` (registro, versão das imagens, hosts, domínio) e crie o Secret.
3. Instale um Ingress Controller, cert-manager (TLS) e metrics-server (HPA).
4. Quando precisar de mais de uma réplica de `notifications`, adicione o backplane Redis ao SignalR.
