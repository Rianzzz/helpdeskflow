// Gera as imagens do README (docs/images) em cima da plataforma REAL rodando.
//
//   docker compose --profile apps up -d --build      (ou o cluster: ./scripts/k8s-kind.sh up)
//   cd web && npm run screenshots                     (BASE_URL=http://localhost:8088 para o Kubernetes)
//
// Cria uma empresa de demonstração com equipe, chamados, uma conversa e uma nota interna, e fotografa as telas.
// Usa a API para montar o cenário (rápido) e o navegador para fotografar (o que o usuário vê de verdade).
import { chromium, request as pwRequest } from '@playwright/test'
import { mkdir } from 'node:fs/promises'
import { dirname, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const BASE_URL = process.env.BASE_URL ?? 'http://localhost:3000'
const OUT = resolve(dirname(fileURLToPath(import.meta.url)), '../../docs/images')
const PASSWORD = 'senhaForte123'
const suffix = Date.now().toString(36)

await mkdir(OUT, { recursive: true })
const api = await pwRequest.newContext({ baseURL: BASE_URL })

async function post(path, data, token) {
  for (let tentativa = 0; ; tentativa++) {
    const r = await api.post(path, { data, headers: token ? { Authorization: `Bearer ${token}` } : {} })
    // O rate limit de autenticação (10/min por IP) é uma proteção de verdade: espera o prazo informado e tenta de novo.
    if (r.status() === 429 && tentativa < 3) {
      const espera = Number(r.headers()['retry-after'] ?? 20)
      console.log(`rate limit em ${path}; aguardando ${espera}s`)
      await new Promise((ok) => setTimeout(ok, espera * 1000))
      continue
    }
    if (!r.ok()) throw new Error(`${path} -> ${r.status()} ${await r.text()}`)
    return r.status() === 204 ? null : r.json().catch(() => null)
  }
}
async function put(path, data, token) {
  const r = await api.put(path, { data, headers: { Authorization: `Bearer ${token}` } })
  if (!r.ok()) throw new Error(`${path} -> ${r.status()} ${await r.text()}`)
}
const login = async (email) => (await post('/api/auth/login', { email, password: PASSWORD })).accessToken

// ── Cenário ───────────────────────────────────────────────────────────────
const adminEmail = `ana@acme-${suffix}.test`
const { tenantId } = await post('/api/auth/register-tenant', {
  companyName: `Acme Suporte ${suffix.slice(-4)}`, adminName: 'Ana Souza', email: adminEmail, password: PASSWORD,
})
for (let i = 0; i < 60; i++) { // espera a saga de onboarding ativar a empresa
  const s = await (await api.get(`/api/auth/tenants/${tenantId}/status`)).json()
  if (s.status === 'Active') break
  if (s.status === 'Failed') throw new Error('o onboarding falhou')
  await new Promise((r) => setTimeout(r, 500))
}
const adminToken = await login(adminEmail)

const agentEmail = `bruno@acme-${suffix}.test`
const customerEmail = `carla@acme-${suffix}.test`
const agent = await post('/api/users', { name: 'Bruno Lima', email: agentEmail, password: PASSWORD, role: 'Agent' }, adminToken)
await post('/api/users', { name: 'Carla Mendes', email: customerEmail, password: PASSWORD, role: 'Customer' }, adminToken)
const agentToken = await login(agentEmail)
const customerToken = await login(customerEmail)

// Os usuários chegam ao Tickets/Notifications por evento (consistência eventual): dá um instante.
await new Promise((r) => setTimeout(r, 2500))

const novo = (title, description, priority) => post('/api/tickets', { title, description, priority }, customerToken)
const t1 = await novo('Impressora do 2º andar não imprime', 'Aparece "offline" desde ontem. Já reiniciei o computador e a impressora.', 'High')
const t2 = await novo('Não consigo acessar o e-mail corporativo', 'A senha foi aceita, mas a caixa de entrada não carrega.', 'Urgent')
const t3 = await novo('Solicitar instalação do Visual Studio', 'Preciso do Visual Studio 2022 para o projeto novo.', 'Low')
const t4 = await novo('VPN cai a cada 10 minutos', 'Acontece só em casa, no escritório funciona normalmente.', 'Medium')
const t5 = await novo('Monitor com a tela piscando', 'Começou depois da troca do cabo HDMI.', 'Low')

await put(`/api/tickets/${t1.id}/assign`, { assigneeId: agent.id }, adminToken)
await put(`/api/tickets/${t4.id}/assign`, { assigneeId: agent.id }, adminToken)
await put(`/api/tickets/${t5.id}/assign`, { assigneeId: agent.id }, adminToken)
await put(`/api/tickets/${t5.id}/resolve`, undefined, agentToken)

// Conversa do chamado da impressora: resposta pública, resposta do cliente e uma nota interna (só a equipe vê).
await post(`/api/tickets/${t1.id}/comments`, { body: 'Oi, Carla! Pode me dizer se a impressora mostra alguma luz vermelha piscando?' }, agentToken)
await post(`/api/tickets/${t1.id}/comments`, { body: 'Mostra sim, uma luz vermelha fixa no painel.' }, customerToken)
await post(`/api/tickets/${t1.id}/comments`, { body: 'Mesmo modelo que travou na semana passada: provável rolo de papel. Verificar o contrato de manutenção.', isInternal: true }, agentToken)
await post(`/api/tickets/${t1.id}/comments`, { body: 'Obrigado! Vou abrir uma ordem de serviço com o fornecedor ainda hoje.' }, agentToken)
await new Promise((r) => setTimeout(r, 2500)) // deixa as notificações chegarem

// ── Fotografias ───────────────────────────────────────────────────────────
const browser = await chromium.launch()
const shot = (page, name, opts = {}) => page.screenshot({ path: `${OUT}/${name}.png`, ...opts })

async function entrar(context, email) {
  const page = await context.newPage()
  await page.goto('/login')
  await page.getByLabel('E-mail').fill(email)
  await page.getByLabel('Senha').fill(PASSWORD)
  await page.getByRole('button', { name: 'Entrar' }).click()
  await page.getByRole('heading', { name: 'Chamados' }).waitFor()
  return page
}
const desktop = { viewport: { width: 1280, height: 800 }, locale: 'pt-BR', baseURL: BASE_URL, deviceScaleFactor: 1 }

// Atendente: lista, conversa, notificações
{
  const ctx = await browser.newContext(desktop)
  const page = await entrar(ctx, agentEmail)
  await page.getByRole('link', { name: /Impressora do 2º andar/ }).waitFor()
  await page.getByText('Ao vivo').waitFor() // espera o SignalR conectar: o indicador do menu fica verde
  await shot(page, 'tickets')

  await page.getByRole('link', { name: /Impressora do 2º andar/ }).click()
  await page.getByRole('list', { name: 'Mensagens do chamado' }).waitFor()
  await page.getByText('Mesmo modelo que travou').waitFor()
  await shot(page, 'ticket-conversation', { fullPage: true })

  await page.getByRole('navigation', { name: 'Principal' }).getByRole('link', { name: /Notificações/ }).click()
  await page.getByRole('heading', { name: 'Notificações' }).waitFor()
  await page.waitForTimeout(800)
  await shot(page, 'notifications')
  await ctx.close()
}

// Administradora: usuários e plano
{
  const ctx = await browser.newContext(desktop)
  const page = await entrar(ctx, adminEmail)
  await page.getByRole('navigation', { name: 'Principal' }).getByRole('link', { name: 'Usuários' }).click()
  await page.getByRole('heading', { name: 'Usuários' }).waitFor()
  await page.waitForTimeout(800)
  await shot(page, 'users')
  await ctx.close()
}

// Celular
{
  const ctx = await browser.newContext({ viewport: { width: 390, height: 844 }, deviceScaleFactor: 2, isMobile: true, hasTouch: true, locale: 'pt-BR', baseURL: BASE_URL })
  const page = await entrar(ctx, customerEmail)
  await page.getByRole('link', { name: /Impressora do 2º andar/ }).waitFor()
  await shot(page, 'mobile-tickets')
  await ctx.close()
}

await browser.close()
await api.dispose()
console.log(`Imagens gravadas em ${OUT}`)
