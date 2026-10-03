import { expect, test } from '@playwright/test'
import { createCompany, createTicketViaUi, createUser, loginViaUi, openAs, uid } from './support/stack'

test.describe('Segurança no navegador', () => {
  test('a página é servida com CSP restritiva e demais cabeçalhos de segurança', async ({ page }) => {
    const response = await page.goto('/login')
    const headers = response!.headers()

    expect(headers['content-security-policy']).toContain("script-src 'self'")
    expect(headers['content-security-policy']).toContain("frame-ancestors 'none'")
    expect(headers['x-content-type-options']).toBe('nosniff')
    expect(headers['x-frame-options']).toBe('DENY')
    expect(headers['referrer-policy']).toBe('no-referrer')
    expect(headers['server']).not.toMatch(/\d/) // não anuncia a versão do nginx
  })

  test('a CSP bloqueia script injetado (defesa em profundidade contra XSS)', async ({ page }) => {
    await page.goto('/login')

    const violation = await page.evaluate(async () => {
      const seen = new Promise<string>((resolve) =>
        document.addEventListener('securitypolicyviolation', (e) => resolve(e.violatedDirective), { once: true }),
      )
      const script = document.createElement('script')
      script.textContent = 'window.__injetado = true'
      document.body.appendChild(script)
      return Promise.race([seen, new Promise<string>((r) => setTimeout(() => r('nenhuma'), 2000))])
    })

    expect(violation).toContain('script-src')
    expect(await page.evaluate(() => (window as unknown as { __injetado?: boolean }).__injetado)).toBeUndefined()
  })

  test('HTML digitado em um chamado é exibido como texto e nunca executa', async ({ page, request }) => {
    const company = await createCompany(request)
    await loginViaUi(page, company.admin.email)
    await page.evaluate(() => {
      ;(window as unknown as { __xss?: number }).__xss = undefined
    })
    const payload = `<img src=x onerror="window.__xss=1"> ${uid()}`

    await createTicketViaUi(page, payload)
    await page.getByRole('link').filter({ hasText: 'img src=x' }).click()

    await expect(page.getByRole('heading', { name: payload })).toBeVisible() // aparece LITERALMENTE como texto
    await expect(page.locator('main img')).toHaveCount(0)
    expect(await page.evaluate(() => (window as unknown as { __xss?: number }).__xss)).toBeUndefined()
  })

  test('o access token nunca é gravado em localStorage nem no sessionStorage', async ({ page, request }) => {
    const company = await createCompany(request)
    await loginViaUi(page, company.admin.email)

    const storage = await page.evaluate(() => ({
      local: Object.entries(localStorage),
      session: Object.entries(sessionStorage),
    }))

    expect(storage.local).toEqual([]) // nada em localStorage
    expect(storage.session.map(([k]) => k)).toEqual(['hdf.refresh']) // só o refresh token, por aba
    const looksLikeJwt = /^[\w-]+\.[\w-]+\.[\w-]+$/
    expect(storage.session.some(([, v]) => looksLikeJwt.test(v))).toBe(false) // e ele NÃO é um JWT
  })

  test('cliente não vê "Usuários" e a URL direta mostra acesso restrito', async ({ browser, request, baseURL }) => {
    const company = await createCompany(request)
    const customer = await createUser(request, company, 'Customer')
    const page = await openAs(browser, baseURL, customer.email)

    await expect(page.getByRole('navigation', { name: 'Principal' }).getByRole('link', { name: 'Usuários' })).toHaveCount(0)

    await page.goto('/users')
    await expect(page.getByText('Acesso restrito')).toBeVisible()
    await page.context().close()
  })

  test('mesmo escondendo botões, o servidor recusa a ação (cliente tentando resolver pela API)', async ({ browser, request, baseURL }) => {
    const company = await createCompany(request)
    const customer = await createUser(request, company, 'Customer')
    const page = await openAs(browser, baseURL, customer.email)
    await createTicketViaUi(page, `Meu chamado ${uid()}`)

    // Pega o token que a própria aplicação usa e tenta resolver "na unha": o servidor decide, não a tela.
    const status = await page.evaluate(async () => {
      const refresh = sessionStorage.getItem('hdf.refresh')
      const auth = await fetch('/api/auth/refresh', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ refreshToken: refresh }),
      }).then((r) => r.json())
      sessionStorage.setItem('hdf.refresh', auth.refreshToken)
      const list = await fetch('/api/tickets', { headers: { Authorization: `Bearer ${auth.accessToken}` } }).then((r) => r.json())
      const resolve = await fetch(`/api/tickets/${list[0].id}/resolve`, {
        method: 'PUT',
        headers: { Authorization: `Bearer ${auth.accessToken}` },
      })
      return resolve.status
    })

    expect(status).toBe(403)
    await page.context().close()
  })
})
