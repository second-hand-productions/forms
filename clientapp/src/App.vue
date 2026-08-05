<script setup>
import { ref } from 'vue'
import FormBuilder from './components/FormBuilder.vue'
import FormFill from './components/FormFill.vue'
import ReportBuilder from './components/ReportBuilder.vue'

// The app deliberately avoids a router (see the note in FormBuilder.vue); this
// top-level view swap keeps that stance. v-if fully unmounts the inactive view,
// so FormKit's drag-and-drop and the TipTap editor never run side by side.
const view = ref('builder')
</script>

<template>
  <main>
    <h1>Form builder spike</h1>

    <nav class="top-nav" aria-label="Sections">
      <button
        type="button"
        class="nav-tab"
        :class="{ current: view === 'builder' }"
        :aria-current="view === 'builder' ? 'page' : undefined"
        data-testid="nav-builder"
        @click="view = 'builder'"
      >
        Form builder
      </button>
      <button
        type="button"
        class="nav-tab"
        :class="{ current: view === 'fill' }"
        :aria-current="view === 'fill' ? 'page' : undefined"
        data-testid="nav-fill"
        @click="view = 'fill'"
      >
        Fill form
      </button>
      <button
        type="button"
        class="nav-tab"
        :class="{ current: view === 'reports' }"
        :aria-current="view === 'reports' ? 'page' : undefined"
        data-testid="nav-reports"
        @click="view = 'reports'"
      >
        Report builder
      </button>
    </nav>

    <template v-if="view === 'builder'">
      <p class="lede">
        Drag to reorder, retype and rename fields, then save. Evaluating what an
        in-house builder costs versus licensing one.
      </p>
      <FormBuilder />
    </template>

    <template v-else-if="view === 'fill'">
      <p class="lede">
        Fill out a saved form and capture the response. Captured responses become
        the real data a report can be rendered against.
      </p>
      <FormFill />
    </template>

    <template v-else>
      <p class="lede">
        Design a report template for a form, drop in fields, and preview it filled
        with a captured response — or sample data. Export to PDF.
      </p>
      <ReportBuilder />
    </template>
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

.top-nav {
  display: flex;
  gap: 0.25rem;
  border-bottom: 1px solid #e2e2e2;
  margin-bottom: 1.5rem;
}

.nav-tab {
  padding: 0.6rem 1rem;
  border: none;
  border-bottom: 2px solid transparent;
  background: transparent;
  cursor: pointer;
  font-size: 1rem;
  color: #666;
}

.nav-tab:hover {
  color: #222;
}

.nav-tab.current {
  color: #1d4ed8;
  border-bottom-color: #1d4ed8;
  font-weight: 600;
}

.lede {
  color: #666;
  margin-bottom: 1.5rem;
}
</style>
