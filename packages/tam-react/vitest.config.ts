import { defineConfig } from 'vitest/config';
import path from 'node:path';

// The package's OWN test harness (Sol re-review round 8 follow-up): tam-react is source-only and was
// previously tested through the ERP sample app's toolchain — the wrong dependency direction. It now
// owns its runner. The @tam aliases mirror how consumers resolve the sources, so tests run against the
// real source, not a build. Pure-logic tests run in 'node'; mounted-component tests (React Testing
// Library — see OperationForm.lifecycle.test.tsx) opt into jsdom per-file via a docblock.
export default defineConfig({
  resolve: {
    alias: {
      '@tam/core': path.resolve(__dirname, '../tam-core/src/index.ts'),
      '@tam/react': path.resolve(__dirname, './src/index.tsx'),
    },
  },
  test: {
    include: ['src/**/*.test.{ts,tsx}'],
    // Default to 'node' for pure logic; a component test opts into jsdom with a
    // `// @vitest-environment jsdom` docblock at the top of its file.
    environment: 'node',
  },
});
