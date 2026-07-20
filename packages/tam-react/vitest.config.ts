import { defineConfig } from 'vitest/config';
import path from 'node:path';

// The package's OWN test harness (Sol re-review round 8 follow-up): tam-react is source-only and was
// previously tested through the ERP sample app's toolchain — the wrong dependency direction. It now
// owns its runner. The @tam aliases mirror how consumers resolve the sources, so tests run against the
// real source, not a build. environment stays 'node' for pure logic; switch to 'jsdom' when a
// component (React Testing Library) test lands.
export default defineConfig({
  resolve: {
    alias: {
      '@tam/core': path.resolve(__dirname, '../tam-core/src/index.ts'),
      '@tam/react': path.resolve(__dirname, './src/index.tsx'),
    },
  },
  test: {
    include: ['src/**/*.test.ts'],
    environment: 'node',
  },
});
