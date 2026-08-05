<script setup>
import { ref, computed, onMounted, watch } from 'vue'
import { FormKitSchema } from '@formkit/vue'
import { toRenderSchema } from '../builder/schemaModel.js'
import { buildSubmissionData } from '../report/submissionData.js'
import { apiUrl } from '../api.js'

/**
 * Fill out a saved form and capture the response. This is the source of the real
 * data reports merge in: a submission is stored as the same flat merge-key map
 * (via buildSubmissionData) that the report renderer looks values up by, so a
 * captured response drops straight into a report with no reshaping.
 *
 * The form renders through the same FormKitSchema + toRenderSchema path the
 * builder's own preview uses, so what a respondent fills is exactly what the
 * builder designed — column spans, conditional requiredness and all.
 */

const forms = ref([]) // {id, name} from GET /api/forms
const selectedFormId = ref('')
const schema = ref(null) // the selected form's stored FormKit schema
const renderSchema = ref([]) // toRenderSchema(schema) — what FormKit renders
const submissions = ref([]) // recent captured responses for the selected form

const loadState = ref({ status: 'idle', message: '' })
const saveState = ref({ status: 'idle', message: '' })

const hasForm = computed(() => Boolean(selectedFormId.value) && renderSchema.value.length > 0)

// Remount the FormKit form when the bound form changes: FormKitSchema patches
// positionally, so reusing the instance across a schema swap would carry the
// previous form's inputs and bindings over.
const formKey = computed(() => `${selectedFormId.value}:${renderSchema.value.length}`)

onMounted(loadForms)

async function loadForms() {
  try {
    const res = await fetch(apiUrl('/forms'))
    if (!res.ok) throw new Error(`HTTP ${res.status}`)
    forms.value = await res.json()
  } catch {
    forms.value = []
  }
}

watch(selectedFormId, async (id) => {
  schema.value = null
  renderSchema.value = []
  submissions.value = []
  saveState.value = { status: 'idle', message: '' }
  if (!id) return

  loadState.value = { status: 'working', message: 'Loading form…' }
  try {
    const [formRes, subsRes] = await Promise.all([
      fetch(apiUrl(`/forms/${id}`)),
      fetch(apiUrl(`/forms/${id}/submissions`)),
    ])
    if (!formRes.ok) throw new Error(`HTTP ${formRes.status}`)
    const form = await formRes.json()
    schema.value = form.schema
    renderSchema.value = toRenderSchema(form.schema)
    submissions.value = subsRes.ok ? await subsRes.json() : []
    loadState.value = { status: 'idle', message: '' }
  } catch (err) {
    loadState.value = { status: 'error', message: err.message }
  }
})

async function onSubmit(payload) {
  if (!selectedFormId.value || !schema.value) return

  const data = buildSubmissionData(schema.value, payload)
  saveState.value = { status: 'saving', message: 'Saving…' }
  try {
    const res = await fetch(apiUrl(`/forms/${selectedFormId.value}/submissions`), {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ data }),
    })
    if (!res.ok) {
      const problem = await res.json().catch(() => null)
      throw new Error(problem?.detail ?? `HTTP ${res.status}`)
    }
    const saved = await res.json()
    // Show it immediately, newest first — the same order the API returns.
    submissions.value = [saved, ...submissions.value]
    saveState.value = { status: 'saved', message: 'Response captured — it’s now available to reports.' }
  } catch (err) {
    saveState.value = { status: 'error', message: err.message }
  }
}

function formatWhen(iso) {
  try {
    return new Date(iso).toLocaleString()
  } catch {
    return iso
  }
}
</script>

<template>
  <div class="fill">
    <section class="panel">
      <label class="field">
        <span>Fill out form</span>
        <select v-model="selectedFormId" data-testid="fill-form-picker">
          <option value="" disabled>Choose a form…</option>
          <option v-for="f in forms" :key="f.id" :value="f.id">{{ f.name }}</option>
        </select>
      </label>
      <p v-if="loadState.status === 'error'" class="error">{{ loadState.message }}</p>
      <p v-else-if="!selectedFormId" class="hint">
        Choose a form to fill it out. Captured responses become the real data a
        report can be rendered against.
      </p>
    </section>

    <section v-if="hasForm" class="panel form-panel">
      <h2>Response</h2>
      <FormKit :key="formKey" type="form" submit-label="Capture response" @submit="onSubmit">
        <FormKitSchema :schema="renderSchema" />
      </FormKit>
      <p
        v-if="saveState.message"
        :class="saveState.status === 'error' ? 'error' : 'ok'"
        data-testid="fill-status"
      >
        {{ saveState.message }}
      </p>
    </section>

    <section v-if="hasForm" class="panel">
      <h2>Recent responses</h2>
      <p v-if="!submissions.length" class="hint">No responses captured yet.</p>
      <ul v-else class="subs" data-testid="fill-submissions">
        <li v-for="s in submissions" :key="s.id">{{ formatWhen(s.createdAt) }}</li>
      </ul>
    </section>
  </div>
</template>

<style scoped>
.fill {
  display: flex;
  flex-direction: column;
  gap: 1.5rem;
  max-width: 46rem;
}

.panel {
  border: 1px solid #ddd;
  border-radius: 6px;
  padding: 1rem;
}

h2 {
  margin: 0 0 0.75rem;
  font-size: 0.9rem;
  text-transform: uppercase;
  letter-spacing: 0.04em;
  color: #666;
}

.field {
  display: flex;
  flex-direction: column;
  gap: 0.25rem;
  font-size: 0.8rem;
  color: #444;
}

.field select {
  padding: 0.4rem 0.5rem;
  border: 1px solid #ccc;
  border-radius: 0.35rem;
  font: inherit;
  font-size: 0.9rem;
}

.subs {
  margin: 0;
  padding-left: 1.2rem;
  font-size: 0.85rem;
  color: #444;
}

.subs li {
  margin: 0.15rem 0;
}

.hint {
  color: #888;
  font-size: 0.8rem;
}

.error {
  color: #b91c1c;
  font-size: 0.85rem;
  margin-top: 0.5rem;
}

.ok {
  color: #15803d;
  font-size: 0.85rem;
  margin-top: 0.5rem;
}
</style>
