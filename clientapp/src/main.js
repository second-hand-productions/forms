import { createApp } from 'vue'
import { plugin as formKitPlugin, defaultConfig } from '@formkit/vue'
import '@formkit/themes/genesis'
import './style.css'
import App from './App.vue'

createApp(App).use(formKitPlugin, defaultConfig()).mount('#app')
