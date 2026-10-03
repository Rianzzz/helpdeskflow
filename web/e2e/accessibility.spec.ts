import AxeBuilder from '@axe-core/playwright'
import { expect, test, type Page } from '@playwright/test'
import { createCompany, createTicketViaApi, loginViaUi, uid } from './support/stack'

/** Falha com a lista legível das violações de acessibilidade (WCAG 2.0/2.1 A e AA). */
async function expectAccessible(page: Page, where: string) {
  const results = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa']).analyze()
  const summary = results.violations.map((v) => `[${v.impact}] ${v.id}: ${v.help} (${v.nodes.length}x) ${v.nodes[0]?.target.join(' ')}`)
  expect(summary, `Violações de acessibilidade em ${where}`).toEqual([])
}

test.describe('Acessibilidade (axe, WCAG AA)', () => {
  test('telas públicas: login, cadastro e acompanhamento', async ({ page, request }) => {
    await page.goto('/login')
    await expect(page.getByRole('heading', { name: 'Entrar' })).toBeVisible()
    await expectAccessible(page, 'login')

    await page.goto('/register')
    await expect(page.getByRole('heading', { name: 'Criar empresa' })).toBeVisible()
    await expectAccessible(page, 'cadastro')

    await page.getByLabel('Senha').fill('curta')
    await page.getByLabel('Senha').blur()
    await expect(page.getByText('Use pelo menos 10 caracteres.')).toBeVisible()
    await expectAccessible(page, 'cadastro com erro de validação')

    const company = await createCompany(request)
    await page.goto(`/register/status/${company.tenantId}`)
    await expect(page.getByRole('heading', { name: 'Empresa pronta!' })).toBeVisible()
    await expectAccessible(page, 'empresa pronta')
  })

  test('área autenticada: chamados, detalhe, notificações, usuários e empresa', async ({ page, request }) => {
    const company = await createCompany(request)
    const title = `Acessível ${uid()}`
    await createTicketViaApi(request, company.admin, title, 'Urgent')
    await loginViaUi(page, company.admin.email)

    await expect(page.getByRole('link', { name: new RegExp(title) })).toBeVisible()
    await expectAccessible(page, 'lista de chamados')

    await page.getByRole('button', { name: 'Novo chamado' }).click()
    await expect(page.getByRole('dialog', { name: 'Novo chamado' })).toBeVisible()
    await expectAccessible(page, 'modal de novo chamado')
    await page.keyboard.press('Escape')

    await page.getByRole('link', { name: new RegExp(title) }).click()
    await expect(page.getByRole('heading', { name: title })).toBeVisible()
    await expectAccessible(page, 'detalhe do chamado')

    await page.getByRole('link', { name: /Notificações/ }).click()
    await expect(page.getByRole('heading', { name: 'Notificações' })).toBeVisible()
    await expectAccessible(page, 'notificações')

    await page.getByRole('link', { name: 'Usuários' }).click()
    await expect(page.getByRole('heading', { name: 'Usuários' })).toBeVisible()
    await expectAccessible(page, 'usuários')

    await page.getByRole('link', { name: 'Empresa' }).click()
    await expect(page.getByText('Limite de usuários')).toBeVisible()
    await expectAccessible(page, 'empresa')
  })

  test('dá para usar o login só com o teclado', async ({ page, request }) => {
    const company = await createCompany(request)
    await page.goto('/login')

    await page.getByLabel('E-mail').focus()
    await page.keyboard.type(company.admin.email)
    await page.keyboard.press('Tab')
    await page.keyboard.type('senhaForte123')
    await page.keyboard.press('Enter')

    await expect(page.getByRole('heading', { name: 'Chamados' })).toBeVisible()
  })
})
