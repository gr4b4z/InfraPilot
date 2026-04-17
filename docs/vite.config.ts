import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

const base = process.env.PAGES_BASE_PATH || '/';

export default defineConfig({
  base,
  plugins: [react()],
  server: {
    host: '0.0.0.0',
    port: 4173,
  },
});
