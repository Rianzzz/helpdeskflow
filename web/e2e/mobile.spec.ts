import { expect, test } from '@playwright/test'
import { createCompany, createTicketViaApi, createTicketViaUi, loginViaUi, uid } from './support/stack'

// Roda no projeto "mobile" (Pixel 7, 412 px de largura).
test.describe('Celular', () => {
  test('menu recolhido, abre pelo botão e a página não rola para os lados', async ({ page, request }) => {
    const company = await createCompany(request)
    await loginViaUi(page, company.admin.email)

    await expect(page.getByRole('navigation', { name: 'Principal' })).toBeHidden()
    await page.getByRole('button', { name: 'Abrir menu' }).click()
    await expect(page.getByRole('navigation', { name: 'Principal' })).toBeVisible()

    await page.getByRole('link', { name: 'Empresa' }).click()
    await expect(page.getByRole('heading', { name: 'Empresa' })).toBeVisible()
    await expect(page.getByRole('navigation', { name: 'Principal' })).toBeHidden() // fecha ao navegar

    const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth)
    expect(overflow, 'nenhuma rolagem horizontal').toBeLessThanOrEqual(0)
  })

  test('abre e acompanha um chamado pelo celular', async ({ page, request }) => {
    const company = await createCompany(request)
    const longTitle = `Chamado com um título bem comprido para testar a quebra de linha no celular ${uid()}`
    await createTicketViaApi(request, company.admin, longTitle, 'High')
    await loginViaUi(page, company.admin.email)

    await expect(page.getByRole('link', { name: new RegExp(longTitle.slice(0, 30)) })).toBeVisible()

    const title = `Pelo celular ${uid()}`
    await createTicketViaUi(page, title, 'Alta')
    await page.getByRole('link', { name: new RegExp(title) }).click()
    await expect(page.getByRole('heading', { name: title })).toBeVisible()
    await expect(page.getByRole('button', { name: 'Marcar como resolvido' })).toBeVisible()

    const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth)
    expect(overflow, 'nenhuma rolagem horizontal').toBeLessThanOrEqual(0)
  })
})
