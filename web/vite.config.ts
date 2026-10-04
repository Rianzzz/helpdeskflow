/// <reference types="vitest/config" />
import tailwindcss from '@tailwindcss/vite'
import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

// O navegador SEMPRE fala com a mesma origem: em desenvolvimento o Vite repassa /api para o gateway;
// em produção o nginx faz o mesmo. Resultado: sem CORS e sem URL de API escrita no código.
const GATEWAY = process.env.VITE_GATEWAY_URL ?? 'http://localhost:5000'

export default defineConfig({
  plugins: [react(), tailwindcss()],
  build: {
    // O CSP do nginx só aceita fontes da própria origem ("font-src 'self'"): nada de fonte embutida como data: URI.
    assetsInlineLimit: 0,
  },
  server: {
    port: 5173,
    proxy: {
      '/api': { target: GATEWAY, changeOrigin: true },
      '/hubs': { target: GATEWAY, changeOrigin: true, ws: true }, // tempo real (SignalR/WebSocket)
    },
  },
  test: {
    environment: 'jsdom',
    setupFiles: ['./src/test/setup.ts'],
    css: false,
    include: ['src/**/*.test.{ts,tsx}'], // os testes de navegador (e2e/) são do Playwright, não do Vitest
  },
})
