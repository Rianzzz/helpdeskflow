import { expect, test } from '@playwright/test'
import { createCompany, loginViaUi, PASSWORD } from './support/stack'

test.describe('Autenticação e sessão', () => {
  test('rota protegida manda para o login e, depois de entrar, volta para onde a pessoa queria ir', async ({ page, request }) => {
    const company = await createCompany(request)

    await page.goto('/notifications')
    await expect(page).toHaveURL(/\/login$/)

    await page.getByLabel('E-mail').fill(company.admin.email)
    await page.getByLabel('Senha').fill(PASSWORD)
    await page.getByRole('button', { name: 'Entrar' }).click()

    await expect(page).toHaveURL(/\/notifications$/)
    await expect(page.getByRole('heading', { name: 'Notificações' })).toBeVisible()
  })

  test('senha errada mostra uma mensagem genérica e não entra', async ({ page, request }) => {
    const company = await createCompany(request)

    await page.goto('/login')
    await page.getByLabel('E-mail').fill(company.admin.email)
    await page.getByLabel('Senha').fill('senhaErrada999')
    await page.getByRole('button', { name: 'Entrar' }).click()

    await expect(page.getByRole('alert')).toContainText('E-mail ou senha incorretos')
    await expect(page).toHaveURL(/\/login$/)
  })

  test('e-mail inexistente recebe exatamente a mesma resposta (não revela quais contas existem)', async ({ page, request }) => {
    const company = await createCompany(request)
    const messageFor = async (email: string) => {
      await page.goto('/login')
      await page.getByLabel('E-mail').fill(email)
      await page.getByLabel('Senha').fill('senhaErrada999')
      await page.getByRole('button', { name: 'Entrar' }).click()
      return page.getByRole('alert').textContent()
    }

    expect(await messageFor('ninguem.assim@e2e.test')).toBe(await messageFor(company.admin.email))
  })

  test('a sessão sobrevive a recarregar a página', async ({ page, request }) => {
    const company = await createCompany(request)
    await loginViaUi(page, company.admin.email)

    await page.reload()

    await expect(page.getByRole('heading', { name: 'Chamados' })).toBeVisible()
    await expect(page.getByText(company.admin.email)).toBeVisible()
  })

  test('sair encerra a sessão: as telas protegidas deixam de abrir, mesmo recarregando', async ({ page, request }) => {
    const company = await createCompany(request)
    await loginViaUi(page, company.admin.email)

    await page.getByRole('button', { name: 'Sair' }).click()
    await expect(page).toHaveURL(/\/login$/)
    expect(await page.evaluate(() => sessionStorage.length)).toBe(0) // o refresh token foi descartado

    await page.goto('/tickets') // tentar voltar às telas protegidas
    await expect(page).toHaveURL(/\/login$/)
    await page.reload()
    await expect(page.getByRole('heading', { name: 'Entrar' })).toBeVisible()
  })

  test('depois de sair, o refresh token antigo não vale mais no servidor', async ({ page, request }) => {
    const company = await createCompany(request)
    await loginViaUi(page, company.admin.email)
    const refreshToken = await page.evaluate(() => sessionStorage.getItem('hdf.refresh'))

    await page.getByRole('button', { name: 'Sair' }).click()
    await expect(page).toHaveURL(/\/login$/)

    const response = await request.post('/api/auth/refresh', { data: { refreshToken } })
    expect(response.status()).toBe(401) // revogado no servidor, não só apagado do navegador
  })

  test('refresh token adulterado derruba a sessão ao recarregar', async ({ page, request }) => {
    const company = await createCompany(request)
    await loginViaUi(page, company.admin.email)

    await page.evaluate(() => sessionStorage.setItem('hdf.refresh', 'token-forjado-pelo-atacante'))
    await page.reload()

    await expect(page).toHaveURL(/\/login$/)
  })

  test('várias requisições com token expirado compartilham UMA renovação e a pessoa continua logada', async ({ page, request }) => {
    // O backend ROTACIONA o refresh token e trata o reuso como roubo. Se cada requisição com 401 renovasse sozinha,
    // a segunda reapresentaria um token já usado e derrubaria a sessão. Aqui simulamos 401 nas DUAS consultas que a
    // tela dispara ao mesmo tempo (chamados e contador de notificações) e contamos as renovações no navegador real.
    const company = await createCompany(request)
    const refreshes: string[] = []
    page.on('request', (r) => {
      if (r.url().endsWith('/api/auth/refresh')) refreshes.push(r.url())
    })

    const expireOnce = (pattern: string) => {
      let served = false
      return page.route(pattern, async (route) => {
        if (served) return route.continue()
        served = true
        await route.fulfill({ status: 401, contentType: 'application/json', body: '{"title":"Não autorizado","status":401}' })
      })
    }
    await expireOnce('**/api/tickets')
    await expireOnce('**/api/notifications?unread=true')

    await loginViaUi(page, company.admin.email)

    await expect(page.getByText('Nenhum chamado por aqui ainda')).toBeVisible() // a lista carregou depois da renovação
    await expect(page).not.toHaveURL(/\/login/) // não foi deslogado
    expect(refreshes).toHaveLength(1)
  })

  test('página inexistente mostra o 404 com caminho de volta', async ({ page }) => {
    await page.goto('/isso-nao-existe')

    await expect(page.getByText('Não encontramos esta página.')).toBeVisible()
    await page.getByRole('button', { name: 'Voltar ao início' }).click()
    await expect(page).toHaveURL(/\/login$/)
  })
})
