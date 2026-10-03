import { execSync } from 'node:child_process'
import path from 'node:path'
import { expect, test, type Page } from '@playwright/test'
import { createCompany, createTicketViaApi, createUser, loginViaUi, menu, openAs, uid } from './support/stack'

/** O indicador do menu mostra o estado da conexão em tempo real (data-status). */
const live = (page: Page) => page.getByTestId('live-indicator')
const unreadBadge = (page: Page) => menu(page).getByLabel(/não lidas/)

test.describe('Notificações em tempo real (SignalR)', () => {
  test('o aviso aparece NA HORA, sem recarregar a página e sem esperar a consulta periódica', async ({ page, request }) => {
    const company = await createCompany(request)
    const customer = await createUser(request, company, 'Customer')
    await loginViaUi(page, company.admin.email)
    await menu(page).getByRole('link', { name: /Notificações/ }).click()
    await expect(live(page)).toHaveAttribute('data-status', 'connected')
    await expect(live(page)).toContainText('Ao vivo')

    await page.evaluate(() => {
      ;(window as unknown as { __semRecarregar?: boolean }).__semRecarregar = true
    })
    const title = `Ao vivo ${uid()}`

    await createTicketViaApi(request, customer, title, 'High') // alguém abre um chamado em outro lugar

    // A consulta periódica só roda a cada 15 s (e nem roda quando está ao vivo): 8 s só passa se o servidor EMPURROU.
    await expect(page.getByRole('main').getByText(`Novo chamado: ${title}`)).toBeVisible({ timeout: 8_000 })
    await expect(page.getByRole('status').filter({ hasText: `Novo chamado: ${title}` })).toBeVisible() // aviso rápido na tela
    await expect(unreadBadge(page)).toBeVisible()
    expect(await page.evaluate(() => (window as unknown as { __semRecarregar?: boolean }).__semRecarregar)).toBe(true)
  })

  test('o contador do menu sobe sozinho em qualquer tela e o chamado novo entra na lista de chamados', async ({ page, request }) => {
    const company = await createCompany(request)
    const customer = await createUser(request, company, 'Customer')
    await loginViaUi(page, company.admin.email)
    await expect(live(page)).toHaveAttribute('data-status', 'connected')
    const title = `Lista ao vivo ${uid()}`

    await createTicketViaApi(request, customer, title)

    await expect(page.getByRole('link', { name: new RegExp(title) })).toBeVisible({ timeout: 8_000 }) // lista de chamados atualizada
    await expect(unreadBadge(page)).toBeVisible()
  })

  test('marcar como lida em uma aba atualiza o contador das outras abas da mesma pessoa', async ({ browser, baseURL, request }) => {
    const company = await createCompany(request)
    await createTicketViaApi(request, company.admin, `Para ler ${uid()}`)
    const tabA = await openAs(browser, baseURL, company.admin.email)
    const tabB = await openAs(browser, baseURL, company.admin.email)
    await expect(live(tabA)).toHaveAttribute('data-status', 'connected')
    await expect(live(tabB)).toHaveAttribute('data-status', 'connected')

    // Espera os avisos iniciais (boas-vindas + novo chamado) chegarem nas duas abas.
    for (const tab of [tabA, tabB]) {
      await expect(async () => {
        await tab.reload()
        await expect(unreadBadge(tab)).toHaveText('2', { timeout: 4_000 })
      }).toPass({ timeout: 30_000 })
    }

    await menu(tabA).getByRole('link', { name: /Notificações/ }).click()
    await tabA.getByRole('button', { name: 'Marcar todas como lidas' }).click()

    // A aba B NÃO foi tocada nem recarregada: o contador some por empurrão do servidor.
    await expect(unreadBadge(tabB)).toHaveCount(0, { timeout: 8_000 })
    await tabA.context().close()
    await tabB.context().close()
  })

  test('se o tempo real estiver bloqueado, a tela cai para a consulta periódica e o aviso chega mesmo assim', async ({ page, request }) => {
    test.setTimeout(90_000)
    const company = await createCompany(request)
    const customer = await createUser(request, company, 'Customer')
    await page.context().route('**/hubs/**', (route) => route.abort()) // proxy corporativo bloqueando tudo de tempo real

    await loginViaUi(page, company.admin.email)
    await menu(page).getByRole('link', { name: /Notificações/ }).click()
    await expect(live(page)).toHaveAttribute('data-status', 'offline')
    await expect(live(page)).toContainText('Atualizando a cada 15 s')

    const title = `Reserva ${uid()}`
    await createTicketViaApi(request, customer, title)

    await expect(page.getByRole('main').getByText(`Novo chamado: ${title}`)).toBeVisible({ timeout: 40_000 })
  })

  test('o token da conexão em tempo real não vaza para a página nem para o armazenamento', async ({ page, request }) => {
    const company = await createCompany(request)
    const urls: string[] = []
    page.on('websocket', (ws) => urls.push(ws.url()))
    await loginViaUi(page, company.admin.email)
    await expect(live(page)).toHaveAttribute('data-status', 'connected')

    expect(page.url()).not.toContain('access_token') // a barra de endereço nunca carrega o token
    expect(await page.evaluate(() => JSON.stringify({ ...localStorage, ...sessionStorage }))).not.toMatch(/eyJ[\w-]+\.[\w-]+\.[\w-]+/)
    expect(urls.length).toBeGreaterThan(0) // houve um WebSocket mesmo...
    expect(urls.every((u) => u.startsWith(`ws://${new URL(page.url()).host}/hubs/`))).toBe(true) // ...e na MESMA origem
  })

  // Reinicia o serviço de verdade: só roda onde o teste pode controlar o Docker (local e CI da plataforma).
  test('depois que o serviço de notificações reinicia, a conexão volta sozinha e nada se perde', async ({ page, request }) => {
    test.skip(!process.env.E2E_CAN_RESTART_SERVICES, 'defina E2E_CAN_RESTART_SERVICES=1 para permitir reiniciar o serviço via Docker')
    test.setTimeout(150_000)
    const company = await createCompany(request)
    const customer = await createUser(request, company, 'Customer')
    await loginViaUi(page, company.admin.email)
    await expect(live(page)).toHaveAttribute('data-status', 'connected')

    // O Playwright roda a partir de web/: a raiz do repositório (onde está o docker-compose.yml) é a pasta acima.
    execSync('docker compose --profile apps restart notifications', { cwd: path.resolve(process.cwd(), '..'), stdio: 'ignore' })

    await expect(live(page)).toHaveAttribute('data-status', 'connected', { timeout: 100_000 })
    const title = `Depois do reinício ${uid()}`
    await createTicketViaApi(request, customer, title)

    await menu(page).getByRole('link', { name: /Notificações/ }).click()
    await expect(page.getByRole('main').getByText(`Novo chamado: ${title}`)).toBeVisible({ timeout: 15_000 })
  })
})
