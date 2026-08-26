import '@testing-library/jest-dom/vitest'
import { cleanup } from '@testing-library/react'
import { afterEach } from 'vitest'

// Every test mounts into the same document; unmounting between them keeps one test's DOM out of the next.
afterEach(() => cleanup())
