import AxeBuilder from '@axe-core/playwright'
import { expect, test } from '@playwright/test'
import { apiLogin, createCompany, createUser, loginViaUi, menu, PASSWORD, uid } from './support/stack'

/** O plano Free comporta 5 usuários (o administrador conta como um). */
test.describe('Limites do plano', () => {
  test('plano lotado: a interface bloqueia e explica, e a API recusa com 409', async ({ page, request }) => {
    const company = await createCompany(request)
    for (let i = 0; i < 4; i++) await createUser(request, company, 'Agent')
    await loginViaUi(page, company.admin.email)

    await menu(page).getByRole('link', { name: 'Usuários' }).click()
    await expect(page.getByText('5 de 5 usuários do plano Free.')).toBeVisible()
    await expect(page.getByRole('button', { name: 'Novo usuário' })).toBeDisabled()
    await expect(page.getByText(/Limite do plano atingido: 5 usuários/)).toBeVisible()

    await menu(page).getByRole('link', { name: 'Empresa' }).click()
    const bar = page.getByRole('progressbar', { name: 'Usuários usados no plano' })
    await expect(bar).toHaveAttribute('aria-valuenow', '5')
    await expect(bar).toHaveAttribute('aria-valuemax', '5')
    await expect(page.getByText(/Plano lotado/)).toBeVisible()

    // Esconder o botão não protege nada: o servidor também recusa.
    const { token } = await apiLogin(request, company.admin.email)
    const response = await request.post('/api/users', {
      headers: { Authorization: `Bearer ${token}` },
      data: { name: 'Extra', email: `extra.${uid()}@e2e.test`, password: PASSWORD, role: 'Agent' },
    })
    expect(response.status()).toBe(409)
    expect(((await response.json()) as { detail: string }).detail).toContain('Limite de usuários do plano atingido (5)')
  })

  test('adicionar o último usuário pela interface enche o plano na hora', async ({ page, request }) => {
    const company = await createCompany(request)
    for (let i = 0; i < 3; i++) await createUser(request, company, 'Customer') // 4 de 5
    await loginViaUi(page, company.admin.email)
    await menu(page).getByRole('link', { name: 'Usuários' }).click()
    await expect(page.getByText('4 de 5 usuários do plano Free.')).toBeVisible()
    await expect(page.getByRole('button', { name: 'Novo usuário' })).toBeEnabled()

    await page.getByRole('button', { name: 'Novo usuário' }).click()
    const dialog = page.getByRole('dialog', { name: 'Novo usuário' })
    await dialog.getByLabel('Nome').fill('Última Vaga')
    await dialog.getByLabel('E-mail').fill(`ultima.${uid()}@e2e.test`)
    await dialog.getByLabel('Senha inicial').fill(PASSWORD)
    await dialog.getByRole('button', { name: 'Adicionar' }).click()

    await expect(page.getByText('5 de 5 usuários do plano Free.')).toBeVisible()
    await expect(page.getByRole('button', { name: 'Novo usuário' })).toBeDisabled()
  })

  test('com a tela desatualizada, o servidor continua sendo a palavra final e o erro é claro', async ({ page, request }) => {
    const company = await createCompany(request)
    for (let i = 0; i < 3; i++) await createUser(request, company, 'Agent') // 4 de 5
    await loginViaUi(page, company.admin.email)
    await menu(page).getByRole('link', { name: 'Usuários' }).click()
    await expect(page.getByText('4 de 5 usuários do plano Free.')).toBeVisible()

    // Outra pessoa (ou outra aba) ocupa a última vaga ENQUANTO esta tela ainda mostra "4 de 5".
    await createUser(request, company, 'Customer')

    await page.getByRole('button', { name: 'Novo usuário' }).click()
    const dialog = page.getByRole('dialog', { name: 'Novo usuário' })
    await dialog.getByLabel('Nome').fill('Chegou Tarde')
    await dialog.getByLabel('E-mail').fill(`tarde.${uid()}@e2e.test`)
    await dialog.getByLabel('Senha inicial').fill(PASSWORD)
    await dialog.getByRole('button', { name: 'Adicionar' }).click()

    await expect(dialog.getByRole('alert')).toContainText('Limite de usuários do plano atingido (5)')
  })

  test('o limite vale por empresa: outra empresa não é afetada', async ({ page, request }) => {
    const cheia = await createCompany(request, 'Cheia')
    for (let i = 0; i < 4; i++) await createUser(request, cheia, 'Agent')
    const livre = await createCompany(request, 'Livre')

    await loginViaUi(page, livre.admin.email)
    await menu(page).getByRole('link', { name: 'Usuários' }).click()

    await expect(page.getByText('1 de 5 usuários do plano Free.')).toBeVisible()
    await expect(page.getByRole('button', { name: 'Novo usuário' })).toBeEnabled()
  })

  test('quem não é administrador vê o plano da empresa, mas não o uso nem a lista de pessoas', async ({ page, request }) => {
    const company = await createCompany(request)
    const agent = await createUser(request, company, 'Agent')
    await loginViaUi(page, agent.email)

    await menu(page).getByRole('link', { name: 'Empresa' }).click()

    await expect(page.getByText('Limite de usuários')).toBeVisible()
    await expect(page.getByRole('progressbar')).toHaveCount(0)
  })

  test('a página de empresa com a barra de uso é acessível (WCAG AA)', async ({ page, request }) => {
    const company = await createCompany(request)
    for (let i = 0; i < 4; i++) await createUser(request, company, 'Agent') // barra no estado "lotado"
    await loginViaUi(page, company.admin.email)
    await menu(page).getByRole('link', { name: 'Empresa' }).click()
    await expect(page.getByRole('progressbar')).toBeVisible()

    const results = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa']).analyze()

    expect(results.violations.map((v) => `[${v.impact}] ${v.id}: ${v.help}`)).toEqual([])
  })
})
