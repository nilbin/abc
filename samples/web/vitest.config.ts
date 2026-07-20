import { defineConfig } from 'vitest/config';
import path from 'node:path';

// Component/unit tests for @tam/react (Sol re-review round 8, Phase 4). The form state machine —
// complete-vs-sparse payloads, refs, debounce, identity, suggestions — earned direct tests. Runs the
// tam-react sources through the same @tam path aliases the app build uses.
export default defineConfig({
  resolve: {
    alias: {
      '@tam/core': path.resolve(__dirname, '../../packages/tam-core/src/index.ts'),
      '@tam/react': path.resolve(__dirname, '../../packages/tam-react/src/index.tsx'),
    },
  },
  test: {
    include: ['../../packages/tam-react/src/**/*.test.ts'],
    environment: 'node',
  },
});
