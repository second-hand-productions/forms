<script setup>
import FormBuilder from './components/FormBuilder.vue'
</script>

<template>
  <main>
    <h1>Form builder spike</h1>
    <p class="lede">
      Drag to reorder, retype and rename fields, then save. Evaluating what an
      in-house builder costs versus licensing one.
    </p>
    <FormBuilder />
  </main>
</template>

<style scoped>
main {
  max-width: 80rem;
  margin: 0 auto;
  padding: 2rem;
  text-align: left;
}

.lede {
  color: #666;
  margin-bottom: 1.5rem;
}
</style>
