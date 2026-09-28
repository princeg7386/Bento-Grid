import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'

export default defineConfig({
  plugins: [react(), tailwindcss()],
  server: {
    port: 5173,
    strictPort: true,
    proxy: {
      // The dashboard talks to the ASP.NET Core API through this proxy in development,
      // so the browser only ever sees one origin.
      '/api': {
        target: 'http://localhost:5179',
        changeOrigin: true,
      },
    },
  },
})
