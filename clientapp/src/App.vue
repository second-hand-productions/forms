<script setup>
import { ref } from 'vue'

const forecasts = ref([])
const apiError = ref('')
const submitted = ref(null)

async function loadForecasts() {
  apiError.value = ''
  try {
    const res = await fetch('/WeatherForecast')
    if (!res.ok) throw new Error(`HTTP ${res.status}`)
    forecasts.value = await res.json()
  } catch (err) {
    apiError.value = err.message
  }
}

function handleSubmit(data) {
  submitted.value = data
}
</script>

<template>
  <main>
    <h1>FormKit + Vue 3</h1>

    <FormKit type="form" submit-label="Submit" @submit="handleSubmit">
      <FormKit type="text" name="name" label="Name" validation="required" />
      <FormKit
        type="email"
        name="email"
        label="Email"
        validation="required|email"
      />
      <FormKit
        type="select"
        name="summary"
        label="Preferred weather"
        :options="['Freezing', 'Chilly', 'Mild', 'Warm', 'Scorching']"
      />
    </FormKit>

    <pre v-if="submitted">{{ submitted }}</pre>

    <h2>API check</h2>
    <button type="button" @click="loadForecasts">Load /WeatherForecast</button>
    <p v-if="apiError">Request failed: {{ apiError }}</p>
    <ul>
      <li v-for="f in forecasts" :key="f.date">
        {{ f.date }} — {{ f.temperatureC }}°C, {{ f.summary }}
      </li>
    </ul>
  </main>
</template>

<style scoped>
main {
  max-width: 32rem;
  margin: 0 auto;
  padding: 2rem;
  text-align: left;
}
</style>
