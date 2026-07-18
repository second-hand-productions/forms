import { defineConfig, loadEnv } from 'vite'
import vue from '@vitejs/plugin-vue'

// Matches sslPort in forms/Properties/launchSettings.json. Override with:
//   VITE_API_URL=https://localhost:<port> npm run dev
const DEFAULT_API_URL = 'https://localhost:8081'

// https://vite.dev/config/
export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, process.cwd(), '')
  const target = env.VITE_API_URL || DEFAULT_API_URL

  return {
    plugins: [vue()],
    server: {
      proxy: {
        '/api': {
          target,
          changeOrigin: true,
          // VS dev certificate is self-signed
          secure: false,
        },
      },
    },
  }
})
