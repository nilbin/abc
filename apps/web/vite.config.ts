import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import path from 'node:path';

export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: {
      '@tam/core': path.resolve(__dirname, '../../packages/tam-core/src/index.ts'),
      '@tam/react': path.resolve(__dirname, '../../packages/tam-react/src/index.tsx'),
    },
  },
  server: {
    port: 5173,
    proxy: { '/api': 'http://localhost:5100' },
  },
  build: {
    outDir: path.resolve(__dirname, '../../samples/erp/wwwroot'),
    emptyOutDir: true,
  },
});
