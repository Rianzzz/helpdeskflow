#!/usr/bin/env bash
# Sobe a plataforma COMPLETA num cluster Kubernetes local (kind) e a derruba depois.
#
#   ./scripts/k8s-kind.sh up        cria o cluster, constrói e carrega as imagens, cria os segredos e aplica os manifestos
#   ./scripts/k8s-kind.sh status    mostra pods, serviços e políticas de rede
#   ./scripts/k8s-kind.sh down      apaga o cluster inteiro
#
# Depois de "up":   front-end http://localhost:8088   |   API (gateway) http://localhost:8089
# Teste:            ./scripts/smoke-test.sh http://localhost:8089 http://localhost:8088
#
# Variáveis: SKIP_BUILD=1 reaproveita as imagens helpdeskflow/*:local que já existem no Docker.
set -euo pipefail
cd "$(dirname "$0")/.."

CLUSTER=helpdeskflow
NS=helpdeskflow
SERVICES=(identity tickets tenants notifications gateway)

build_images() {
  echo "▶ Construindo as imagens (tag local)"
  for svc in "${SERVICES[@]}"; do
    docker build -q -f docker/Dockerfile --target "$svc" -t "helpdeskflow/$svc:local" . >/dev/null
  done
  docker build -q -t helpdeskflow/web:local web >/dev/null
}

load_images() {
  echo "▶ Carregando as imagens no cluster"
  for img in "${SERVICES[@]}" web; do
    kind load docker-image "helpdeskflow/$img:local" --name "$CLUSTER" >/dev/null
  done
}

create_secrets() {
  # Os segredos são gerados AQUI, aleatórios, e nunca ficam em arquivo nem no Git. Se já existem, não são trocados.
  if kubectl -n "$NS" get secret helpdeskflow-secrets >/dev/null 2>&1; then
    echo "▶ Segredos já existem (mantidos)"
  else
    echo "▶ Criando os segredos (valores aleatórios)"
    kubectl -n "$NS" create secret generic helpdeskflow-secrets \
      --from-literal=jwt-signing-key="$(openssl rand -base64 48)" \
      --from-literal=postgres-password="$(openssl rand -hex 16)" \
      --from-literal=rabbitmq-password="$(openssl rand -hex 16)" >/dev/null
  fi
}

wait_ready() {
  echo "▶ Aguardando tudo ficar pronto"
  kubectl -n "$NS" rollout status statefulset/postgres statefulset/rabbitmq --timeout=240s
  for d in "${SERVICES[@]}" web; do
    kubectl -n "$NS" rollout status "deployment/$d" --timeout=300s
  done
}

case "${1:-up}" in
  up)
    kind get clusters 2>/dev/null | grep -qx "$CLUSTER" || kind create cluster --config k8s/kind/cluster.yaml
    kubectl config use-context "kind-$CLUSTER" >/dev/null
    [ -n "${SKIP_BUILD:-}" ] || build_images
    load_images
    kubectl apply -f k8s/base/namespace.yaml >/dev/null
    create_secrets
    echo "▶ Aplicando os manifestos (overlay kind)"
    kubectl apply -k k8s/overlays/kind
    wait_ready
    echo
    echo "✓ Pronto.  Front-end: http://localhost:8088   |   API: http://localhost:8089"
    ;;
  status)
    kubectl -n "$NS" get pods,svc,networkpolicy -o wide
    ;;
  down)
    kind delete cluster --name "$CLUSTER"
    ;;
  *)
    echo "uso: $0 up|status|down" >&2
    exit 1
    ;;
esac
