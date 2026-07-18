import { createApp } from 'vue'
import { plugin as formKitPlugin, defaultConfig } from '@formkit/vue'
import { createMultiStepPlugin } from '@formkit/addons'
import '@formkit/themes/genesis'
import '@formkit/addons/css/multistep'
import './style.css'
import App from './App.vue'

createApp(App)
  .use(
    formKitPlugin,
    defaultConfig({ plugins: [createMultiStepPlugin()] })
  )
  .mount('#app')
