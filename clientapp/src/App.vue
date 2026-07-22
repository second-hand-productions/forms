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
/*
 * width + border-box rather than letting the flex parent size this: #app is a
 * column flex container, and `margin: 0 auto` on a flex item suppresses the
 * cross-axis stretch, so main would shrink-wrap its content and re-centre on
 * every page change — the narrow describe and review pages sat further right
 * than the full-width builder.
 */
main {
  width: 100%;
  max-width: 80rem;
  margin: 0 auto;
  padding: 2rem;
  box-sizing: border-box;
  text-align: left;
}

.lede {
  color: #666;
  margin-bottom: 1.5rem;
}
</style>
