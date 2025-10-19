import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import path from 'path'

// https://vitejs.dev/config/
export default defineConfig({
  plugins: [react()],
  base: '/monitoring/', // Absolute base path to fix asset loading on reload
  resolve: {
    alias: {
      '@': path.resolve(__dirname, './src'),
    },
  },
  build: {
    outDir: '../wwwroot', // Build to parent wwwroot folder
    emptyOutDir: true,
    rollupOptions: {
      output: {
        manualChunks: {
          'react-vendor': ['react', 'react-dom', 'react-router-dom'],
          'chart-vendor': ['recharts'],
          'signalr-vendor': ['@microsoft/signalr'],
          'query-vendor': ['@tanstack/react-query'],
        },
      },
    },
  },
  server: {
    port: 5173,
    proxy: {
      '/monitoring/api': {
        target: 'http://localhost:5000', // Development backend
        changeOrigin: true,
      },
      '/monitoring/monitor': {
        target: 'http://localhost:5000',
        changeOrigin: true,
        ws: true, // WebSocket support for SignalR
      },
    },
  },
})
