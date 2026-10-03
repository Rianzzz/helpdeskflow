import { expect, type APIRequestContext, type Browser, type Page } from '@playwright/test'

export const PASSWORD = 'senhaForte123'

export type Role = 'Admin' | 'Agent' | 'Customer'

export interface Person {
  id: string
  name: string
  email: string
  role: Role
}

export interface Company {
  tenantId: string
  name: string
  admin: Person
}

/** Identificador curto e único: deixa os testes independentes entre si e repetíveis. */
export const uid = () => `${Date.now().toString(36)}${Math.random().toString(36).slice(2, 7)}`

// ───────────── Preparação de cenário pela API (rápida) ─────────────

/** Cadastra uma empresa pela API e espera a SAGA de onboarding concluir. */
export async function createCompany(request: APIRequestContext, prefix = 'Empresa'): Promise<Company> {
  const id = uid()
  const name = `${prefix} ${id}`
  const email = `admin.${id}@e2e.test`

  const response = await request.post('/api/auth/register-tenant', {
    data: { companyName: name, adminName: 'Ana Admin', email, password: PASSWORD },
  })
  expect(response.status(), 'o cadastro deve ser aceito (202)').toBe(202)
  const { tenantId } = (await response.json()) as { tenantId: string }

  await expect
    .poll(async () => ((await (await request.get(`/api/auth/tenants/${tenantId}/status`)).json()) as { status: string }).status, {
      message: 'a saga de onboarding deve ativar a empresa',
      timeout: 30_000,
    })
    .toBe('Active')

  const login = await apiLogin(request, email)
  return { tenantId, name, admin: { id: login.userId, name: 'Ana Admin', email, role: 'Admin' } }
}

export async function apiLogin(request: APIRequestContext, email: string) {
  const response = await request.post('/api/auth/login', { data: { email, password: PASSWORD } })
  expect(response.ok(), `login de ${email}`).toBeTruthy()
  const body = (await response.json()) as { accessToken: string; user: { id: string } }
  return { token: body.accessToken, userId: body.user.id }
}

export async function createUser(request: APIRequestContext, company: Company, role: Exclude<Role, 'Admin'>, name?: string): Promise<Person> {
  const { token } = await apiLogin(request, company.admin.email)
  const id = uid()
  const person = { name: name ?? `${role === 'Agent' ? 'Atendente' : 'Cliente'} ${id}`, email: `${role.toLowerCase()}.${id}@e2e.test`, role }

  const response = await request.post('/api/users', {
    headers: { Authorization: `Bearer ${token}` },
    data: { ...person, password: PASSWORD },
  })
  expect(response.status(), `criar ${role}`).toBe(201)
  const created = (await response.json()) as { id: string }
  return { id: created.id, ...person }
}

export async function createTicketViaApi(request: APIRequestContext, who: Person, title: string, priority = 'Low') {
  const { token } = await apiLogin(request, who.email)
  const response = await request.post('/api/tickets', {
    headers: { Authorization: `Bearer ${token}` },
    data: { title, description: 'criado pelo teste', priority },
  })
  expect(response.status()).toBe(201)
  return ((await response.json()) as { id: string }).id
}

// ───────────── Ações pela interface ─────────────

export async function loginViaUi(page: Page, email: string, password = PASSWORD) {
  await page.goto('/login')
  await page.getByLabel('E-mail').fill(email)
  await page.getByLabel('Senha').fill(password)
  await page.getByRole('button', { name: 'Entrar' }).click()
  await expect(page.getByRole('heading', { name: 'Chamados' })).toBeVisible()
}

/** Abre uma "pessoa" (navegador isolado, com a própria sessão) já logada. Permite testar vários usuários ao mesmo tempo. */
export async function openAs(browser: Browser, baseURL: string | undefined, email: string): Promise<Page> {
  const context = await browser.newContext({ baseURL, locale: 'pt-BR' })
  const page = await context.newPage()
  await loginViaUi(page, email)
  return page
}

/** O menu lateral. Escopo importante: a página de detalhe também tem um link "← Chamados" (voltar). */
export const menu = (page: Page) => page.getByRole('navigation', { name: 'Principal' })

/**
 * Espera uma notificação aparecer. Os eventos chegam "logo depois" (RabbitMQ), e a lista de notificações só é
 * buscada ao abrir a página (ou após 10 s), então recarregamos a tela até o aviso aparecer.
 */
export async function expectNotification(page: Page, text: string) {
  await menu(page).getByRole('link', { name: /Notificações/ }).click()
  await expect(async () => {
    await page.reload()
    await expect(page.getByText(text)).toBeVisible({ timeout: 4_000 })
  }).toPass({ timeout: 30_000 })
}

/** Procura o chamado pelo título na lista e abre o detalhe. */
export async function openTicket(page: Page, title: string) {
  await menu(page).getByRole('link', { name: 'Chamados' }).click()
  const link = page.getByRole('link', { name: new RegExp(title) })
  // Chamados criados por OUTRA pessoa aparecem na lista quando ela se atualiza (a cada 15 s) ou ao recarregar.
  await expect(async () => {
    if (!(await link.isVisible())) await page.reload()
    await expect(link).toBeVisible({ timeout: 4_000 })
  }).toPass({ timeout: 30_000 })
  await link.click()
  await expect(page.getByRole('heading', { name: title })).toBeVisible()
}

export async function createTicketViaUi(page: Page, title: string, priority: 'Baixa' | 'Média' | 'Alta' | 'Urgente' = 'Média') {
  await page.getByRole('button', { name: 'Novo chamado' }).click()
  const dialog = page.getByRole('dialog', { name: 'Novo chamado' })
  await dialog.getByLabel('Título').fill(title)
  await dialog.getByLabel('Descrição').fill('Descrição escrita pelo teste de navegador.')
  await dialog.getByLabel('Prioridade').selectOption({ label: priority })
  await dialog.getByRole('button', { name: 'Abrir chamado' }).click()
  await expect(page.getByText('Chamado aberto! A equipe já foi avisada.')).toBeVisible()
}
