import { defineConfig, mergeConfig } from 'vitest/config'
import viteConfig from './vite.config'

// The UI's own test run: same resolver and same plugins as the build, so a component under test is the
// component that ships. jsdom because everything here is behaviour a browser performs.
export default mergeConfig(
  viteConfig,
  defineConfig({
    test: {
      environment: 'jsdom',
      globals: false,
      setupFiles: ['./src/test/setup.ts'],
      include: ['src/**/*.test.{ts,tsx}'],
      restoreMocks: true,
    },
  })
)
