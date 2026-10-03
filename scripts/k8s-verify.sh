#!/usr/bin/env bash
# Verifica as GARANTIAS que só existem no Kubernetes (o smoke-test.sh cobre o comportamento da aplicação):
#   1. Pod Security "restricted": um pod privilegiado é recusado pelo cluster.
#   2. NetworkPolicy: cada serviço alcança só o que a arquitetura permite; um pod intruso não alcança nada.
#   3. Réplicas: com 2 réplicas de cada serviço sem estado, matar pods não derruba a plataforma.
#
# Uso: ./scripts/k8s-verify.sh [namespace]      (precisa do cluster já no ar: ./scripts/k8s-kind.sh up)
set -uo pipefail
NS="${1:-helpdeskflow}"
falhas=0

ok()   { echo "  ✓ $1"; }
falha() { echo "  ✗ $1"; falhas=$((falhas + 1)); }

# alcanca <pod> <host> <porta>: sucesso se a conexão TCP abre em até 3 s.
alcanca() { kubectl -n "$NS" exec "$1" -- sh -c "nc -z -w 3 $2 $3" >/dev/null 2>&1; }

espera() { # espera <descrição> <esperado: abre|bloqueado> <pod> <host> <porta>
  local desc="$1" esperado="$2"; shift 2
  if alcanca "$@"; then real=abre; else real=bloqueado; fi
  if [ "$real" = "$esperado" ]; then ok "$desc ($real)"; else falha "$desc: esperava $esperado, veio $real"; fi
}

echo "▶ Pod Security (restricted)"
saida="$(kubectl -n "$NS" apply --dry-run=server -f - 2>&1 <<'EOF'
apiVersion: v1
kind: Pod
metadata: { name: privilegiado }
spec:
  containers:
    - name: c
      image: nginx
      securityContext: { privileged: true }
EOF
)"
if echo "$saida" | grep -q "violates PodSecurity"; then ok "pod privilegiado recusado pelo cluster"; else falha "pod privilegiado foi aceito: $saida"; fi

echo "▶ NetworkPolicy"
web="$(kubectl -n "$NS" get pod -l app.kubernetes.io/name=web -o name | head -n1)"
espera "web -> gateway:8080"                      abre      "$web" gateway 8080
espera "web -> tickets:8080 (pulando o gateway)"  bloqueado "$web" tickets 8080
espera "web -> postgres:5432"                     bloqueado "$web" postgres 5432
espera "web -> rabbitmq:5672"                     bloqueado "$web" rabbitmq 5672
espera "web -> internet (1.1.1.1:443)"            bloqueado "$web" 1.1.1.1 443

kubectl -n "$NS" apply -f - >/dev/null <<'EOF'
apiVersion: v1
kind: Pod
metadata: { name: intruso }
spec:
  automountServiceAccountToken: false
  securityContext: { runAsNonRoot: true, runAsUser: 1000, seccompProfile: { type: RuntimeDefault } }
  containers:
    - name: c
      image: nginxinc/nginx-unprivileged:alpine
      command: ["sleep", "300"]
      securityContext: { allowPrivilegeEscalation: false, capabilities: { drop: ["ALL"] } }
EOF
kubectl -n "$NS" wait --for=condition=Ready pod/intruso --timeout=120s >/dev/null
espera "intruso -> postgres:5432"  bloqueado pod/intruso postgres 5432
espera "intruso -> rabbitmq:5672"  bloqueado pod/intruso rabbitmq 5672
espera "intruso -> identity:8080"  bloqueado pod/intruso identity 8080
kubectl -n "$NS" delete pod intruso --wait=false >/dev/null

echo "▶ Réplicas: matar pods não derruba a plataforma"
for d in identity tickets tenants gateway web; do kubectl -n "$NS" scale "deployment/$d" --replicas=2 >/dev/null; done
for d in identity tickets tenants gateway web; do kubectl -n "$NS" rollout status "deployment/$d" --timeout=240s >/dev/null; done
ok "2 réplicas de cada serviço sem estado no ar"

web_url="${WEB_URL:-http://localhost:8088}"
erros=0; total=0
for rodada in 1 2 3; do
  for d in identity tickets tenants gateway web; do
    pod="$(kubectl -n "$NS" get pod -l app.kubernetes.io/name=$d -o name | head -n1)"
    kubectl -n "$NS" delete "$pod" --wait=false >/dev/null
    # Enquanto o pod morre/renasce, a plataforma tem que continuar respondendo (o outro pod atende).
    for i in $(seq 1 15); do
      total=$((total + 1))
      code="$(curl -s -o /dev/null -w '%{http_code}' --max-time 5 "$web_url/api/tickets" || true)"
      [ "$code" = "401" ] || erros=$((erros + 1))   # 401 = chegou até o gateway e foi barrado por falta de token
      sleep 0.4
    done
  done
done
for d in identity tickets tenants gateway web; do kubectl -n "$NS" rollout status "deployment/$d" --timeout=240s >/dev/null; done
# Tolerância mínima: a conexão que já estava aberta no instante exato da morte de um pod pode falhar.
if [ "$erros" -le 2 ]; then ok "$total requisições durante 15 mortes de pods: $erros falha(s)"; else falha "$erros de $total requisições falharam durante a morte de pods"; fi

echo
if [ "$falhas" -eq 0 ]; then echo "✓ Garantias do Kubernetes verificadas."; else echo "✗ $falhas verificação(ões) falharam."; exit 1; fi
