import { expect, test } from '@playwright/test'
import { createCompany, loginViaUi, PASSWORD, uid } from './support/stack'

test.describe('Cadastro de empresa (saga de onboarding)', () => {
  test('cria a empresa, acompanha o provisionamento e entra como administrador', async ({ page }) => {
    const id = uid()
    const email = `ana.${id}@e2e.test`

    await page.goto('/register')
    await page.getByLabel('Nome da empresa').fill(`Acme ${id}`)
    await page.getByLabel('Seu nome').fill('Ana Admin')
    await page.getByLabel('E-mail').fill(email)
    await page.getByLabel('Senha').fill(PASSWORD)
    await page.getByRole('button', { name: 'Criar empresa' }).click()

    // O backend passa por Identity → Tenants → Identity. A tela acompanha até concluir.
    await expect(page).toHaveURL(/\/register\/status\//)
    await expect(page.getByRole('heading', { name: 'Empresa pronta!' })).toBeVisible({ timeout: 30_000 })

    await page.getByRole('button', { name: 'Entrar agora' }).click()
    await expect(page.getByLabel('E-mail')).toHaveValue(email) // o e-mail já vem preenchido
    await expect(page.getByText('Empresa criada com sucesso!')).toBeVisible()

    await page.getByLabel('Senha').fill(PASSWORD)
    await page.getByRole('button', { name: 'Entrar' }).click()

    await expect(page.getByRole('heading', { name: 'Chamados' })).toBeVisible()
    await expect(page.getByText('Nenhum chamado por aqui ainda')).toBeVisible()
    await expect(page.getByText('Administrador')).toBeVisible()
  })

  test('mostra os dados e o plano da empresa recém-criada', async ({ page, request }) => {
    const company = await createCompany(request, 'Plano')
    await loginViaUi(page, company.admin.email)

    await page.getByRole('link', { name: 'Empresa' }).click()

    await expect(page.getByRole('heading', { name: 'Empresa' })).toBeVisible()
    await expect(page.getByText(company.name)).toBeVisible()
    await expect(page.getByText('Free')).toBeVisible()
    await expect(page.getByText('Limite de usuários')).toBeVisible()
  })

  test('recusa nome de empresa duplicado, explica o motivo e deixa tentar de novo', async ({ page, request }) => {
    const first = await createCompany(request, 'Duplicada')
    const email = `beto.${uid()}@e2e.test`

    await page.goto('/register')
    await page.getByLabel('Nome da empresa').fill(`  ${first.name.toUpperCase()}  `) // outra caixa e espaços: é a MESMA empresa
    await page.getByLabel('Seu nome').fill('Beto')
    await page.getByLabel('E-mail').fill(email)
    await page.getByLabel('Senha').fill(PASSWORD)
    await page.getByRole('button', { name: 'Criar empresa' }).click()

    await expect(page.getByRole('heading', { name: 'Não foi possível criar a empresa' })).toBeVisible({ timeout: 30_000 })
    await expect(page.getByText('Já existe uma empresa com este nome.')).toBeVisible()

    // Compensação da saga: nada ficou criado, e o e-mail está livre; os campos voltam preenchidos.
    await page.getByRole('button', { name: 'Tentar novamente' }).click()
    await expect(page.getByText(/O cadastro anterior não foi concluído/)).toBeVisible()
    await expect(page.getByLabel('E-mail')).toHaveValue(email)

    await page.getByLabel('Nome da empresa').fill(`Outra ${uid()}`)
    await page.getByLabel('Senha').fill(PASSWORD)
    await page.getByRole('button', { name: 'Criar empresa' }).click()
    await expect(page.getByRole('heading', { name: 'Empresa pronta!' })).toBeVisible({ timeout: 30_000 })
  })

  test('nome reservado é recusado', async ({ page }) => {
    await page.goto('/register')
    await page.getByLabel('Nome da empresa').fill('Admin')
    await page.getByLabel('Seu nome').fill('Zeca')
    await page.getByLabel('E-mail').fill(`zeca.${uid()}@e2e.test`)
    await page.getByLabel('Senha').fill(PASSWORD)
    await page.getByRole('button', { name: 'Criar empresa' }).click()

    await expect(page.getByText('Este nome de empresa é reservado.')).toBeVisible({ timeout: 30_000 })
  })

  test('senha fraca é barrada na hora, sem enviar nada ao servidor', async ({ page }) => {
    let called = false
    page.on('request', (r) => {
      if (r.url().includes('/api/auth/register-tenant')) called = true
    })

    await page.goto('/register')
    await page.getByLabel('Nome da empresa').fill('Acme')
    await page.getByLabel('Seu nome').fill('Ana')
    await page.getByLabel('E-mail').fill('ana@e2e.test')
    await page.getByLabel('Senha').fill('curta1')
    await page.getByRole('button', { name: 'Criar empresa' }).click()

    await expect(page.getByText('Use pelo menos 10 caracteres.')).toBeVisible()
    await expect(page).toHaveURL(/\/register$/)
    expect(called).toBe(false)
  })

  test('recarregar a página de acompanhamento continua funcionando', async ({ page, request }) => {
    // Regressão: o acompanhamento ficava girando para sempre ao recarregar (cache limpo na montagem).
    const id = uid()
    const response = await request.post('/api/auth/register-tenant', {
      data: { companyName: `Reload ${id}`, adminName: 'Rita', email: `rita.${id}@e2e.test`, password: PASSWORD },
    })
    const { tenantId } = (await response.json()) as { tenantId: string }

    await page.goto(`/register/status/${tenantId}`)
    await page.reload()

    await expect(page.getByRole('heading', { name: 'Empresa pronta!' })).toBeVisible({ timeout: 30_000 })
  })
})
