import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import tailwindcss from '@tailwindcss/vite';
import { VitePWA } from 'vite-plugin-pwa';

export default defineConfig({
  plugins: [
    react(),
    tailwindcss(),
    VitePWA({
      registerType: 'autoUpdate',
      includeAssets: ['favicon.svg', 'mic-uplink-worklet.js'],
      manifest: {
        name: 'Nereus',
        short_name: 'Nereus',
        description: 'Nereus WebSDR control surface',
        theme_color: '#0a1220',
        background_color: '#0a1220',
        display: 'standalone',
        orientation: 'any',
        start_url: '/',
        scope: '/',
        icons: [
          { src: 'nereus-icon-192.png', sizes: '192x192', type: 'image/png', purpose: 'any' },
          { src: 'nereus-icon-512.png', sizes: '512x512', type: 'image/png', purpose: 'any' },
          { src: 'nereus-icon-512-maskable.png', sizes: '512x512', type: 'image/png', purpose: 'maskable' },
          { src: 'nereus-icon.svg', sizes: 'any', type: 'image/svg+xml', purpose: 'any' },
        ],
      },
      workbox: {
        globPatterns: ['**/*.{js,css,html,svg,png,woff2}'],
        navigateFallback: '/index.html',
        navigateFallbackDenylist: [/^\/api/, /^\/ws/],
        runtimeCaching: [
          {
            urlPattern: /^https:\/\/fonts\.googleapis\.com\//,
            handler: 'StaleWhileRevalidate',
            options: { cacheName: 'google-fonts-stylesheets' },
          },
          {
            urlPattern: /^https:\/\/fonts\.gstatic\.com\//,
            handler: 'CacheFirst',
            options: {
              cacheName: 'google-fonts-webfonts',
              expiration: { maxEntries: 20, maxAgeSeconds: 60 * 60 * 24 * 365 },
              cacheableResponse: { statuses: [0, 200] },
            },
          },
        ],
      },
    }),
  ],
  server: {
    port: 5173,
    allowedHosts: ['.ngrok-free.app', '.ngrok.app', '.ngrok.io'],
    proxy: {
      '/api': 'http://localhost:6060',
      '/ws': { target: 'ws://localhost:6060', ws: true },
    },
  },
  build: {
    outDir: '../Nereus.Server/wwwroot',
    emptyOutDir: true,
  },
});
