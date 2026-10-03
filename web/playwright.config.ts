import { defineConfig, devices } from '@playwright/test'

/**
 * Testes de navegador (end-to-end) contra a plataforma COMPLETA em contêineres:
 *   docker compose --profile apps up -d --build      (front em http://localhost:3000)
 *   npm run e2e
 *
 * Para apontar para outro ambiente: E2E_BASE_URL=https://staging.exemplo.com npm run e2e
 *
 * Os testes criam empresas e usuários novos (nomes aleatórios), então podem rodar quantas vezes quiser e em
 * paralelo, sem limpar nada. Eles fazem muitos logins por minuto: suba a plataforma com
 * AUTH_RATE_LIMIT_PER_MINUTE=1000 para o limite de tentativas não atrapalhar.
 */
export default defineConfig({
  testDir: './e2e',
  timeout: 60_000,
  expect: { timeout: 10_000 },
  fullyParallel: true,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 1 : 0,
  workers: process.env.CI ? 2 : 3,
  reporter: process.env.CI ? [['github'], ['html', { open: 'never' }]] : [['list'], ['html', { open: 'never' }]],

  use: {
    baseURL: process.env.E2E_BASE_URL ?? 'http://localhost:3000',
    locale: 'pt-BR',
    timezoneId: 'America/Sao_Paulo',
    trace: 'on-first-retry', // gravação passo a passo para depurar falhas
    screenshot: 'only-on-failure',
  },

  projects: [
    { name: 'desktop', use: { ...devices['Desktop Chrome'] }, testIgnore: /mobile\.spec/ },
    { name: 'mobile', use: { ...devices['Pixel 7'] }, testMatch: /mobile\.spec/ },
  ],
})
