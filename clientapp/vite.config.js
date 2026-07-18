import { defineConfig, loadEnv } from 'vite'
import vue from '@vitejs/plugin-vue'

// The .NET container is launched by Visual Studio with publishAllPorts, so its
// host port changes on every recreate. Override without editing this file:
//   VITE_API_URL=https://localhost:<port> npm run dev
const DEFAULT_API_URL = 'https://localhost:32773'

// https://vite.dev/config/
export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, process.cwd(), '')
  const target = env.VITE_API_URL || DEFAULT_API_URL

  return {
    plugins: [vue()],
    server: {
      proxy: {
        '/WeatherForecast': {
          target,
          changeOrigin: true,
          // VS dev certificate is self-signed
          secure: false,
        },
      },
    },
  }
})
