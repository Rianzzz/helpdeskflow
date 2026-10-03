import AxeBuilder from '@axe-core/playwright'
import { expect, test, type Page } from '@playwright/test'
import { apiLogin, createCompany, createTicketViaApi, createUser, menu, openAs, openTicket, uid } from './support/stack'

const thread = (page: Page) => page.getByRole('list', { name: 'Mensagens do chamado' })
const composer = (page: Page) => page.getByLabel(/^(Sua mensagem|Nota interna \(invisível para o cliente\))$/)

async function send(page: Page, text: string, { internal = false }: { internal?: boolean } = {}) {
  if (internal) await page.getByLabel('Nota interna (só a equipe vê)').check()
  await composer(page).fill(text)
  await page.getByRole('button', { name: internal ? 'Adicionar nota interna' : 'Enviar' }).click()
  await expect(thread(page).getByText(text, { exact: true })).toBeVisible()
  if (internal) await page.getByLabel('Nota interna (só a equipe vê)').uncheck()
}

test.describe('Conversa nos chamados', () => {
  test('cliente e atendente conversam, e cada mensagem aparece NA HORA na tela do outro', async ({ browser, baseURL, request }) => {
    test.setTimeout(120_000)
    const company = await createCompany(request)
    const agent = await createUser(request, company, 'Agent')
    const customer = await createUser(request, company, 'Customer')
    const title = `Conversa ${uid()}`
    await createTicketViaApi(request, customer, title)
    const customerPage = await openAs(browser, baseURL, customer.email)
    const agentPage = await openAs(browser, baseURL, agent.email)

    await openTicket(customerPage, title)
    await openTicket(agentPage, title)
    await expect(customerPage.getByText(/Ainda não há mensagens/)).toBeVisible()
    await expect(menu(customerPage).locator('xpath=..').getByTestId('live-indicator')).toHaveAttribute('data-status', 'connected')

    // O atendente responde. A tela do cliente (já aberta, sem recarregar) mostra a mensagem sozinha: o aviso em
    // tempo real chega e a conversa é recarregada. 15 s seria a consulta periódica: aqui o prazo é bem menor.
    await send(agentPage, 'Olá! Pode reiniciar o equipamento?')
    await expect(thread(customerPage).getByText('Olá! Pode reiniciar o equipamento?')).toBeVisible({ timeout: 10_000 })

    // O cliente responde, e a tela do atendente também atualiza sozinha.
    await send(customerPage, 'Reiniciei e voltou a funcionar, obrigado!')
    await expect(thread(agentPage).getByText('Reiniciei e voltou a funcionar, obrigado!')).toBeVisible({ timeout: 10_000 })

    // Os avisos também chegaram (contador do menu).
    await expect(menu(customerPage).getByLabel(/não lidas/)).toBeVisible()
    await customerPage.context().close()
    await agentPage.context().close()
  })

  test('nota interna: a equipe vê em destaque; o cliente NUNCA vê nem é avisado', async ({ browser, baseURL, request }) => {
    test.setTimeout(120_000)
    const company = await createCompany(request)
    const agent = await createUser(request, company, 'Agent')
    const customer = await createUser(request, company, 'Customer')
    const title = `Nota ${uid()}`
    await createTicketViaApi(request, customer, title)
    const customerPage = await openAs(browser, baseURL, customer.email)
    const agentPage = await openAs(browser, baseURL, agent.email)
    await openTicket(customerPage, title)
    await openTicket(agentPage, title)

    await send(agentPage, 'SEGREDO: este cliente já abriu 3 chamados hoje.', { internal: true })
    const internalItem = thread(agentPage).getByRole('listitem').filter({ hasText: 'SEGREDO' })
    await expect(internalItem).toHaveAttribute('data-internal', 'true')
    await expect(internalItem.getByText('Nota interna')).toBeVisible()

    // Uma resposta pública DEPOIS da nota serve de "sentinela": quando o cliente a recebe, a nota já teria chegado.
    await send(agentPage, 'Resposta pública para o cliente.')
    await expect(thread(customerPage).getByText('Resposta pública para o cliente.')).toBeVisible({ timeout: 10_000 })

    await expect(thread(customerPage).getByRole('listitem')).toHaveCount(1) // só a pública
    await expect(customerPage.getByText('SEGREDO')).toHaveCount(0)
    await customerPage.reload()
    await expect(thread(customerPage).getByText('Resposta pública para o cliente.')).toBeVisible()
    await expect(customerPage.getByText('SEGREDO')).toHaveCount(0)

    // Nem nos avisos: o cliente tem "Nova resposta", mas nenhum "Nota interna".
    await menu(customerPage).getByRole('link', { name: /Notificações/ }).click()
    await expect(customerPage.getByRole('main').getByText(`Nova resposta em: ${title}`)).toBeVisible()
    await expect(customerPage.getByText(/Nota interna/)).toHaveCount(0)
    await customerPage.context().close()
    await agentPage.context().close()
  })

  test('o cliente não tem a opção de nota interna, e o servidor também a recusa (403)', async ({ browser, baseURL, request }) => {
    const company = await createCompany(request)
    const customer = await createUser(request, company, 'Customer')
    const title = `Sem nota ${uid()}`
    const ticketId = await createTicketViaApi(request, customer, title)
    const page = await openAs(browser, baseURL, customer.email)
    await openTicket(page, title)

    await expect(composer(page)).toBeVisible()
    await expect(page.getByLabel(/Nota interna/)).toHaveCount(0)

    // Esconder a opção não protege nada: pela API, o servidor recusa.
    const { token } = await apiLogin(request, customer.email)
    const response = await request.post(`/api/tickets/${ticketId}/comments`, {
      headers: { Authorization: `Bearer ${token}` },
      data: { body: 'tentando uma nota interna', isInternal: true },
    })
    expect(response.status()).toBe(403)
    await page.context().close()
  })

  test('HTML digitado na conversa aparece como texto e nunca executa', async ({ browser, baseURL, request }) => {
    const company = await createCompany(request)
    const customer = await createUser(request, company, 'Customer')
    const title = `XSS ${uid()}`
    await createTicketViaApi(request, customer, title)
    const page = await openAs(browser, baseURL, customer.email)
    await openTicket(page, title)
    const payload = '<img src=x onerror="window.__xss=1"> <script>window.__xss=2</script>'

    await composer(page).fill(payload)
    await page.getByRole('button', { name: 'Enviar' }).click()

    await expect(thread(page).getByText(payload, { exact: true })).toBeVisible() // LITERALMENTE como texto
    await expect(thread(page).locator('img, script')).toHaveCount(0)
    expect(await page.evaluate(() => (window as unknown as { __xss?: number }).__xss)).toBeUndefined()
    await page.context().close()
  })

  test('preserva quebras de linha, aceita Ctrl+Enter e mostra o contador', async ({ browser, baseURL, request }) => {
    const company = await createCompany(request)
    const customer = await createUser(request, company, 'Customer')
    const title = `Teclado ${uid()}`
    await createTicketViaApi(request, customer, title)
    const page = await openAs(browser, baseURL, customer.email)
    await openTicket(page, title)

    await composer(page).fill('Primeira linha')
    await composer(page).press('Shift+Enter')
    await composer(page).pressSequentially('Segunda linha')
    await expect(page.getByText(/\d+\/4000/)).toContainText('/4000')
    await composer(page).press('Control+Enter')

    const item = thread(page).getByRole('listitem').first()
    await expect(item).toContainText('Primeira linha')
    await expect(item).toContainText('Segunda linha')
    expect(await item.locator('p').evaluate((el) => getComputedStyle(el).whiteSpace)).toBe('pre-wrap')
    await expect(composer(page)).toHaveValue('') // campo limpo depois de enviar
    await page.context().close()
  })

  test('chamado fechado não aceita mensagens até ser reaberto', async ({ browser, baseURL, request }) => {
    const company = await createCompany(request)
    const agent = await createUser(request, company, 'Agent')
    const customer = await createUser(request, company, 'Customer')
    const title = `Fechado ${uid()}`
    const ticketId = await createTicketViaApi(request, customer, title)
    const { token } = await apiLogin(request, agent.email)
    for (const action of ['resolve', 'close']) {
      await request.put(`/api/tickets/${ticketId}/${action}`, { headers: { Authorization: `Bearer ${token}` } })
    }
    const page = await openAs(browser, baseURL, customer.email)
    await openTicket(page, title)

    await expect(page.getByText(/Este chamado está fechado/)).toBeVisible()
    await expect(composer(page)).toHaveCount(0)

    await page.getByRole('button', { name: 'Reabrir' }).click()
    await expect(composer(page)).toBeVisible() // reaberto: a conversa volta
    await page.context().close()
  })

  test('outra empresa não lê nem escreve na conversa, nem como administradora', async ({ browser, baseURL, request }) => {
    const alpha = await createCompany(request, 'Alfa')
    const customer = await createUser(request, alpha, 'Customer')
    const ticketId = await createTicketViaApi(request, customer, `Privado ${uid()}`)
    const beta = await createCompany(request, 'Beta')
    const betaPage = await openAs(browser, baseURL, beta.admin.email)

    await betaPage.goto(`/tickets/${ticketId}`)
    await expect(betaPage.getByText('Chamado não encontrado (ou você não tem acesso a ele).')).toBeVisible()

    const { token } = await apiLogin(request, beta.admin.email)
    const read = await request.get(`/api/tickets/${ticketId}/comments`, { headers: { Authorization: `Bearer ${token}` } })
    const write = await request.post(`/api/tickets/${ticketId}/comments`, {
      headers: { Authorization: `Bearer ${token}` },
      data: { body: 'invasão', isInternal: false },
    })
    expect([read.status(), write.status()]).toEqual([404, 404])
    await betaPage.context().close()
  })

  test('a conversa (com nota interna e respostas) é acessível: WCAG AA', async ({ browser, baseURL, request }) => {
    const company = await createCompany(request)
    const agent = await createUser(request, company, 'Agent')
    const customer = await createUser(request, company, 'Customer')
    const title = `Acessível ${uid()}`
    await createTicketViaApi(request, customer, title)
    const page = await openAs(browser, baseURL, agent.email)
    await openTicket(page, title)
    await send(page, 'Resposta pública.')
    await send(page, 'Nota interna de teste.', { internal: true })
    await page.getByLabel('Nota interna (só a equipe vê)').check() // compositor também no modo "nota interna"

    const results = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa']).analyze()

    expect(results.violations.map((v) => `[${v.impact}] ${v.id}: ${v.help} ${v.nodes[0]?.target.join(' ')}`)).toEqual([])
    await page.context().close()
  })
})
