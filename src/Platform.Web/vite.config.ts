import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'
import path from 'path'

/**
 * Where the dev server forwards `/api` and `/agent`. Override when the API runs somewhere else:
 *
 *   VITE_API_TARGET=http://localhost:5300 npm run dev
 */
const apiTarget = process.env.VITE_API_TARGET ?? 'http://localhost:5259'

export default defineConfig({
  plugins: [react(), tailwindcss()],
  define: {
    __APP_VERSION__: JSON.stringify(process.env.APP_VERSION ?? 'dev'),
  },
  resolve: {
    alias: {
      '@': path.resolve(__dirname, './src'),
    },
  },
  server: {
    host: '0.0.0.0',
    // Preferred, not required. The API no longer cares which port the dev server lands on — see the
    // proxy below — so a busy 5173 can fall through to another port instead of failing to start.
    // `PORT` is honoured so a supervising tool can assign one.
    port: Number(process.env.PORT) || 5173,
    /**
     * Proxy the backend through this origin, which is what production already does: the container's
     * nginx forwards `/api/` and `/agent/` to the API, and `BACKEND_BASE_URL` is empty by default so
     * the app uses relative paths.
     *
     * Dev used to be the odd one out — `public/config.json` pointed the browser straight at
     * `http://localhost:5259`, which made every request cross-origin and left the app dependent on the
     * API's CORS allow-list naming the exact dev-server port. Any port but 5173 and the whole app
     * loaded but showed nothing, with only a console CORS error to explain it.
     *
     * Proxying removes the cross-origin request rather than permitting it, so there is no allow-list
     * to keep in step and no port coupling left to break.
     */
    proxy: {
      // The realtime hub needs its own entry: `/api` below pins `ws: false`, which would refuse
      // the SignalR WebSocket upgrade. Longer prefixes win, so this one catches the hub traffic.
      '/api/hubs': {
        target: apiTarget,
        changeOrigin: true,
        ws: true,
      },
      '/api': {
        target: apiTarget,
        changeOrigin: true,
        ws: false,
      },
      '/agent': {
        target: apiTarget,
        changeOrigin: true,
      },
    },
  },
})
