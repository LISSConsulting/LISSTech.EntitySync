import { fileURLToPath, URL } from 'node:url'
import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

export default defineConfig({
  plugins: [react()],
  build: {
    outDir: fileURLToPath(new URL('../scheduler/wwwroot', import.meta.url)),
    assetsDir: 'assets',
    emptyOutDir: true,
    sourcemap: false,
  },
  server: {
    proxy: {
      '/dashboard/data': 'http://localhost:8080',
    },
  },
})
