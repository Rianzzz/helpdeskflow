#!/usr/bin/env bash
# Teste de fumaça da plataforma COMPLETA, pelo gateway: cadastro de empresa (saga), login, chamado e notificação.
#
#   ./scripts/smoke-test.sh [URL_DO_GATEWAY] [URL_DO_FRONT]
#       padrões: http://localhost:5000  e  (sem front: só testa a API)
#   Se a URL do front for informada (ex.: http://localhost:3000), também valida o nginx: SPA, proxy da API e cabeçalhos.
#
# Sai com código diferente de zero se qualquer etapa falhar (usado no CI).
set -euo pipefail

GATEWAY="${1:-http://localhost:5000}"
WEB="${2:-}"
BASE="$GATEWAY/api"
SUFFIX="$RANDOM$RANDOM"
JSON='Content-Type: application/json'
PASSWORD='senhaForte123'

step() { printf '\n▶ %s\n' "$1"; }
fail() { printf '✗ FALHOU: %s\n' "$1" >&2; exit 1; }
field() { grep -o "\"$1\":\"[^\"]*\"" | head -1 | cut -d'"' -f4; }  # extrai um campo string de um JSON simples

wait_until() { # descrição, tentativas, comando...
  local what="$1" tries="$2"; shift 2
  for _ in $(seq 1 "$tries"); do
    if "$@"; then return 0; fi
    sleep 1
  done
  fail "tempo esgotado esperando: $what"
}

step "Gateway saudável"
curl -fsS "$GATEWAY/health" >/dev/null || fail "gateway não respondeu em $GATEWAY/health"

step "Sem token é negado (401)"
code=$(curl -s -o /dev/null -w '%{http_code}' "$BASE/tickets")
[ "$code" = 401 ] || fail "esperava 401 sem token, veio $code"

step "Cadastrar empresa (202 Accepted: provisionamento assíncrono)"
resp=$(curl -sS -w '\n%{http_code}' -X POST "$BASE/auth/register-tenant" -H "$JSON" \
  -d "{\"companyName\":\"Smoke $SUFFIX\",\"adminName\":\"Admin\",\"email\":\"admin$SUFFIX@smoke.test\",\"password\":\"$PASSWORD\"}")
[ "$(echo "$resp" | tail -1)" = 202 ] || fail "cadastro deveria responder 202: $resp"
TENANT_ID=$(echo "$resp" | head -1 | field tenantId)
[ -n "$TENANT_ID" ] || fail "tenantId ausente na resposta"

step "Esperar a saga ativar a empresa (Identity -> Tenants -> Identity)"
tenant_active() { [ "$(curl -s "$BASE/auth/tenants/$TENANT_ID/status" | field status)" = "Active" ]; }
wait_until "empresa ficar Active" 60 tenant_active

step "Login do administrador"
ADMIN_TOKEN=$(curl -fsS -X POST "$BASE/auth/login" -H "$JSON" \
  -d "{\"email\":\"admin$SUFFIX@smoke.test\",\"password\":\"$PASSWORD\"}" | field accessToken)
[ -n "$ADMIN_TOKEN" ] || fail "login sem accessToken"

step "Perfil e plano no serviço Tenants"
curl -fsS "$BASE/tenants/me" -H "Authorization: Bearer $ADMIN_TOKEN" | grep -q '"plan":"Free"' || fail "perfil Free não encontrado"

step "Admin cria um cliente e o cliente abre um chamado"
curl -fsS -o /dev/null -X POST "$BASE/users" -H "$JSON" -H "Authorization: Bearer $ADMIN_TOKEN" \
  -d "{\"name\":\"Cliente\",\"email\":\"cliente$SUFFIX@smoke.test\",\"password\":\"$PASSWORD\",\"role\":\"Customer\"}"
CUSTOMER_TOKEN=$(curl -fsS -X POST "$BASE/auth/login" -H "$JSON" \
  -d "{\"email\":\"cliente$SUFFIX@smoke.test\",\"password\":\"$PASSWORD\"}" | field accessToken)
curl -fsS -o /dev/null -X POST "$BASE/tickets" -H "$JSON" -H "Authorization: Bearer $CUSTOMER_TOKEN" \
  -d '{"title":"Chamado do smoke test","description":"","priority":"Low"}'

step "Cliente enxerga só o próprio chamado"
[ "$(curl -fsS "$BASE/tickets" -H "Authorization: Bearer $CUSTOMER_TOKEN" | grep -o '"title"' | wc -l)" = 1 ] || fail "cliente deveria ver 1 chamado"

step "Admin é notificado (evento Tickets -> Notifications)"
admin_notified() { curl -s "$BASE/notifications" -H "Authorization: Bearer $ADMIN_TOKEN" | grep -q 'Novo chamado: Chamado do smoke test'; }
wait_until "notificação 'Novo chamado' do admin" 30 admin_notified

step "Cliente NÃO pode resolver chamados (403)"
ID=$(curl -fsS "$BASE/tickets" -H "Authorization: Bearer $CUSTOMER_TOKEN" | field id)
code=$(curl -s -o /dev/null -w '%{http_code}' -X PUT "$BASE/tickets/$ID/resolve" -H "Authorization: Bearer $CUSTOMER_TOKEN")
[ "$code" = 403 ] || fail "esperava 403, veio $code"

if [ -n "$WEB" ]; then
  step "Front-end (nginx): página inicial e rota do React Router (fallback para index.html)"
  curl -fsS "$WEB/" | grep -q '<div id="root">' || fail "index.html do front não foi servido em $WEB/"
  curl -fsS "$WEB/tickets/qualquer-id" | grep -q '<div id="root">' || fail "fallback de SPA não funcionou"

  step "Front-end: cabeçalhos de segurança (CSP restritiva, nosniff, sem versão do nginx)"
  headers=$(curl -fsS -D - -o /dev/null "$WEB/")
  echo "$headers" | grep -qi "^content-security-policy:.*script-src 'self'" || fail "CSP ausente ou frouxa"
  echo "$headers" | grep -qi "^x-content-type-options: nosniff" || fail "nosniff ausente"
  echo "$headers" | grep -qi "^x-frame-options: DENY" || fail "X-Frame-Options ausente"
  if echo "$headers" | grep -qiE "^server: nginx/[0-9]"; then fail "o nginx está anunciando a versão"; fi

  step "Front-end: /api passa pelo nginx até o gateway (401 sem token; login funciona pela origem do front)"
  code=$(curl -s -o /dev/null -w '%{http_code}' "$WEB/api/tickets")
  [ "$code" = 401 ] || fail "esperava 401 em $WEB/api/tickets, veio $code"
  VIA_WEB_TOKEN=$(curl -fsS -X POST "$WEB/api/auth/login" -H "$JSON" \
    -d "{\"email\":\"admin$SUFFIX@smoke.test\",\"password\":\"$PASSWORD\"}" | field accessToken)
  [ -n "$VIA_WEB_TOKEN" ] || fail "login pela origem do front falhou"

  step "Tempo real (SignalR): a negociação passa pelo nginx e pelo gateway; sem token é recusada"
  code=$(curl -s -o /dev/null -w '%{http_code}' -X POST "$WEB/hubs/notifications/negotiate?negotiateVersion=1&access_token=$VIA_WEB_TOKEN")
  [ "$code" = 200 ] || fail "negociação do hub com token deveria dar 200, veio $code"
  code=$(curl -s -o /dev/null -w '%{http_code}' -X POST "$WEB/hubs/notifications/negotiate?negotiateVersion=1")
  [ "$code" = 401 ] || fail "negociação do hub SEM token deveria dar 401, veio $code"
  code=$(curl -s -o /dev/null -w '%{http_code}' "$WEB/api/tickets?access_token=$VIA_WEB_TOKEN")
  [ "$code" = 401 ] || fail "token na query só pode valer nas rotas /hubs (em /api deveria dar 401, veio $code)"

  # O token viaja na URL (limitação dos WebSockets de navegador): garantimos que NUNCA é gravado nos logs.
  if command -v docker >/dev/null 2>&1 && docker compose version >/dev/null 2>&1; then
    step "Tempo real: o token da URL não aparece nos logs do nginx, do gateway nem do Notifications"
    if (cd "$(dirname "$0")/.." && docker compose --profile apps logs --no-color web gateway notifications 2>/dev/null | grep -qF "$VIA_WEB_TOKEN"); then
      fail "o JWT apareceu em logs de contêiner"
    fi
  fi
fi

printf '\n✓ Smoke test passou: saga de onboarding, autenticação, isolamento, eventos e notificações funcionando.\n'
