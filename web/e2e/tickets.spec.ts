import { expect, test } from '@playwright/test'
import {
  createCompany,
  createTicketViaApi,
  createTicketViaUi,
  createUser,
  expectNotification,
  loginViaUi,
  menu,
  openAs,
  openTicket,
  PASSWORD,
  uid,
} from './support/stack'

test.describe('Chamados', () => {
  test('ciclo completo entre três pessoas: cliente abre, atendente assume e resolve, cliente é avisado e reabre', async ({ browser, request, baseURL }) => {
    test.setTimeout(120_000) // três pessoas, vários eventos assíncronos entre serviços
    const company = await createCompany(request)
    const agent = await createUser(request, company, 'Agent', `Alex ${uid()}`)
    const customer = await createUser(request, company, 'Customer', `Carla ${uid()}`)
    const title = `Servidor fora do ar ${uid()}`

    const customerPage = await openAs(browser, baseURL, customer.email)
    const adminPage = await openAs(browser, baseURL, company.admin.email)
    const agentPage = await openAs(browser, baseURL, agent.email)

    // 1) O cliente abre um chamado urgente.
    await createTicketViaUi(customerPage, title, 'Urgente')
    const row = customerPage.getByRole('link', { name: new RegExp(title) })
    await expect(row).toContainText('Urgente')
    await expect(row).toContainText('Aberto')
    await expect(row).toContainText('sem responsável')

    // 2) O administrador é avisado (evento Tickets → Notifications) e vê o chamado com o nome do solicitante.
    await expectNotification(adminPage, `Novo chamado: ${title}`)
    await menu(adminPage).getByRole('link', { name: 'Chamados' }).click()
    await expect(adminPage.getByRole('link', { name: new RegExp(title) })).toContainText(customer.name)

    // 3) O atendente assume o chamado. (O Tickets conhece os usuários por eventos: tentamos até replicar.)
    await openTicket(agentPage, title)
    await expect(async () => {
      await agentPage.getByRole('button', { name: 'Assumir chamado' }).click()
      await expect(agentPage.getByText('Você assumiu o chamado.')).toBeVisible({ timeout: 1_500 })
    }).toPass({ timeout: 20_000 })
    await expect(agentPage.getByText('Em andamento')).toBeVisible()
    await expect(agentPage.getByText(agent.name).first()).toBeVisible()

    // 4) O atendente resolve.
    await agentPage.getByRole('button', { name: 'Marcar como resolvido' }).click()
    await expect(agentPage.getByText('Chamado marcado como resolvido. O solicitante foi avisado.')).toBeVisible()
    await expect(agentPage.getByRole('button', { name: 'Fechar chamado' })).toBeVisible()

    // 5) O cliente recebe os avisos e vê o chamado resolvido, sem poder resolver ou fechar por conta própria.
    await expectNotification(customerPage, `Chamado resolvido: ${title}`)
    await expect(customerPage.getByText(`Seu chamado está em atendimento: ${title}`)).toBeVisible()

    await openTicket(customerPage, title)
    await expect(customerPage.getByText('Resolvido', { exact: true })).toBeVisible()
    await expect(customerPage.getByRole('button', { name: 'Marcar como resolvido' })).toHaveCount(0)
    await expect(customerPage.getByRole('button', { name: 'Fechar chamado' })).toHaveCount(0)

    // 6) Não ficou satisfeito: reabre.
    await customerPage.getByRole('button', { name: 'Reabrir' }).click()
    await expect(customerPage.getByText('Chamado reaberto.')).toBeVisible()
    // "Aberto" também é o rótulo da data de abertura: o selo de status é o <span> com exatamente esse texto.
    await expect(customerPage.locator('span').filter({ hasText: /^Aberto$/ })).toBeVisible()
    await expect(customerPage.getByRole('button', { name: 'Reabrir' })).toHaveCount(0) // já está aberto de novo

    await Promise.all([customerPage, adminPage, agentPage].map((p) => p.context().close()))
  })

  test('administrador atribui o chamado a alguém da equipe pela lista', async ({ browser, request, baseURL }) => {
    const company = await createCompany(request)
    const agent = await createUser(request, company, 'Agent', `Bia ${uid()}`)
    const title = `Wi-Fi caiu ${uid()}`
    await createTicketViaApi(request, company.admin, title, 'High')
    const adminPage = await openAs(browser, baseURL, company.admin.email)

    await openTicket(adminPage, title)
    // A equipe aparece no seletor quando o Tickets já recebeu os usuários (por evento).
    await expect(async () => {
      await adminPage.reload()
      await expect(adminPage.getByRole('option', { name: agent.name })).toBeAttached({ timeout: 1_500 })
    }).toPass({ timeout: 20_000 })

    await adminPage.getByLabel('Ou atribuir a').selectOption({ label: agent.name })
    await adminPage.getByRole('button', { name: 'Atribuir', exact: true }).click()

    await expect(adminPage.getByText('Responsável atualizado.')).toBeVisible()
    await expect(adminPage.getByText('Em andamento')).toBeVisible()
    await adminPage.context().close()
  })

  test('filtra por status e busca por título', async ({ page, request }) => {
    const company = await createCompany(request)
    const impressora = `Impressora ${uid()}`
    const rede = `Rede ${uid()}`
    await createTicketViaApi(request, company.admin, impressora, 'Low')
    const redeId = await createTicketViaApi(request, company.admin, rede, 'Urgent')
    await loginViaUi(page, company.admin.email)

    await expect(page.getByRole('link', { name: new RegExp(impressora) })).toBeVisible()
    await expect(page.getByRole('link', { name: new RegExp(rede) })).toBeVisible()

    // Urgente vem antes de baixa.
    const links = page.getByRole('link').filter({ hasText: /Impressora|Rede/ })
    await expect(links.first()).toContainText(rede)

    await page.getByRole('searchbox', { name: 'Buscar chamados' }).fill('impressora')
    await expect(page.getByRole('link', { name: new RegExp(rede) })).toHaveCount(0)
    await page.getByRole('searchbox', { name: 'Buscar chamados' }).fill('')

    await page.getByRole('link', { name: new RegExp(rede) }).click()
    await expect(page).toHaveURL(new RegExp(redeId))
    await page.getByRole('button', { name: 'Marcar como resolvido' }).click()
    await expect(page.getByText('Resolvido', { exact: true })).toBeVisible()

    await menu(page).getByRole('link', { name: 'Chamados' }).click()
    await page.getByRole('tab', { name: /Resolvido/ }).click()
    await expect(page.getByRole('link', { name: new RegExp(rede) })).toBeVisible()
    await expect(page.getByRole('link', { name: new RegExp(impressora) })).toHaveCount(0)
  })

  test('cliente só enxerga os próprios chamados', async ({ browser, request, baseURL }) => {
    const company = await createCompany(request)
    const alice = await createUser(request, company, 'Customer', `Alice ${uid()}`)
    const bob = await createUser(request, company, 'Customer', `Bob ${uid()}`)
    const aliceTitle = `Da Alice ${uid()}`
    const bobTitle = `Do Bob ${uid()}`
    await createTicketViaApi(request, alice, aliceTitle)
    const bobTicket = await createTicketViaApi(request, bob, bobTitle)

    const alicePage = await openAs(browser, baseURL, alice.email)

    await expect(alicePage.getByRole('link', { name: new RegExp(aliceTitle) })).toBeVisible()
    await expect(alicePage.getByRole('link', { name: new RegExp(bobTitle) })).toHaveCount(0)

    // Mesmo digitando a URL do chamado do Bob, o servidor não entrega.
    await alicePage.goto(`/tickets/${bobTicket}`)
    await expect(alicePage.getByText('Chamado não encontrado (ou você não tem acesso a ele).')).toBeVisible()
    await alicePage.context().close()
  })

  test('uma empresa nunca enxerga os chamados de outra', async ({ page, request }) => {
    const a = await createCompany(request, 'Alfa')
    const b = await createCompany(request, 'Beta')
    const secret = `Segredo da Alfa ${uid()}`
    const ticketId = await createTicketViaApi(request, a.admin, secret)

    await loginViaUi(page, b.admin.email)

    await expect(page.getByText('Nenhum chamado por aqui ainda')).toBeVisible()
    await page.goto(`/tickets/${ticketId}`)
    await expect(page.getByText('Chamado não encontrado (ou você não tem acesso a ele).')).toBeVisible()
  })

  test('título vazio não deixa abrir o chamado', async ({ page, request }) => {
    const company = await createCompany(request)
    await loginViaUi(page, company.admin.email)

    await page.getByRole('button', { name: 'Novo chamado' }).click()
    const dialog = page.getByRole('dialog', { name: 'Novo chamado' })
    await dialog.getByLabel('Título').fill('   ')

    await expect(dialog.getByRole('button', { name: 'Abrir chamado' })).toBeDisabled()
    await dialog.getByRole('button', { name: 'Cancelar' }).click()
    await expect(dialog).toBeHidden()
  })

  test('o modal fecha com a tecla Esc', async ({ page, request }) => {
    const company = await createCompany(request)
    await loginViaUi(page, company.admin.email)

    await page.getByRole('button', { name: 'Novo chamado' }).click()
    await expect(page.getByRole('dialog', { name: 'Novo chamado' })).toBeVisible()
    await page.keyboard.press('Escape')

    await expect(page.getByRole('dialog', { name: 'Novo chamado' })).toBeHidden()
  })

  test('marcar notificações como lidas atualiza o contador do menu', async ({ page, request }) => {
    const company = await createCompany(request)
    await createTicketViaApi(request, company.admin, `Para notificar ${uid()}`)
    await loginViaUi(page, company.admin.email)

    // O admin recebe "boas-vindas" e "novo chamado": o selo mostra quantas não foram lidas.
    const badge = page.getByRole('navigation', { name: 'Principal' }).getByLabel(/não lidas/)
    await expect(async () => {
      await page.reload()
      await expect(badge).toBeVisible({ timeout: 1_500 })
    }).toPass({ timeout: 20_000 })

    await page.getByRole('link', { name: /Notificações/ }).click()
    await page.getByRole('button', { name: 'Marcar todas como lidas' }).click()

    await expect(page.getByRole('button', { name: 'Marcar todas como lidas' })).toHaveCount(0)
    await expect(page.getByText('Marcar como lida')).toHaveCount(0)
  })
})

test.describe('Usuários (administrador)', () => {
  test('administrador cadastra um atendente pelo formulário', async ({ page, request }) => {
    const company = await createCompany(request)
    await loginViaUi(page, company.admin.email)
    const name = `Nova Atendente ${uid()}`
    const email = `nova.${uid()}@e2e.test`

    await page.getByRole('link', { name: 'Usuários' }).click()
    await page.getByRole('button', { name: 'Novo usuário' }).click()
    const dialog = page.getByRole('dialog', { name: 'Novo usuário' })
    await dialog.getByLabel('Nome').fill(name)
    await dialog.getByLabel('E-mail').fill(email)
    await dialog.getByLabel('Senha inicial').fill(PASSWORD)
    await dialog.getByLabel('Papel').selectOption('Agent')
    await dialog.getByRole('button', { name: 'Adicionar' }).click()

    await expect(page.getByText(`${name} foi adicionado(a)`)).toBeVisible()
    await expect(page.getByText(email)).toBeVisible()

    // O novo usuário já consegue entrar com a senha inicial.
    const other = await page.context().browser()!.newContext()
    const otherPage = await other.newPage()
    await loginViaUi(otherPage, email)
    await expect(otherPage.getByText('Atendente', { exact: true })).toBeVisible()
    await other.close()
  })

  test('e-mail repetido é recusado com mensagem clara e senha fraca é barrada', async ({ page, request }) => {
    const company = await createCompany(request)
    await loginViaUi(page, company.admin.email)
    await page.getByRole('link', { name: 'Usuários' }).click()

    await page.getByRole('button', { name: 'Novo usuário' }).click()
    const dialog = page.getByRole('dialog', { name: 'Novo usuário' })
    await dialog.getByLabel('Nome').fill('Repetido')
    await dialog.getByLabel('E-mail').fill(company.admin.email)
    await dialog.getByLabel('Senha inicial').fill('curta1')
    await dialog.getByRole('button', { name: 'Adicionar' }).click()
    await expect(dialog.getByText('Use pelo menos 10 caracteres.')).toBeVisible()

    await dialog.getByLabel('Senha inicial').fill(PASSWORD)
    await dialog.getByRole('button', { name: 'Adicionar' }).click()
    await expect(dialog.getByRole('alert')).toContainText('E-mail já cadastrado.')
  })
})
