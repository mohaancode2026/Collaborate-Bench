import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    // API_URL lets you point the dev server at another API (e.g. a second copy on another port); default is the usual 5080.
    proxy: { '/api': process.env.API_URL ?? 'http://localhost:5080' },
  },
  build: { outDir: 'dist', chunkSizeWarningLimit: 1500 },
});
