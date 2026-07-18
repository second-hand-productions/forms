<script setup>
import { computed, ref, watch } from 'vue'
import { FormKitSchema } from '@formkit/vue'
import { useDragAndDrop } from '@formkit/drag-and-drop/vue'
import { FIELD_TYPES, getFieldType } from '../builder/fieldTypes.js'
import {
  createNode,
  createStep,
  findDuplicateNames,
  fromSchema,
  isStep,
  retypeNode,
  toSchema,
} from '../builder/schemaModel.js'

// useDragAndDrop owns the array: `nodes` is reordered in place on drop.
const [listRef, nodes] = useDragAndDrop(
  [createNode('text'), createNode('email')],
  { dragHandle: '.drag-handle' }
)

const selectedUid = ref(nodes.value[0]?._uid ?? null)
const formName = ref('Untitled form')
const saveState = ref({ status: 'idle', message: '' })
const submitted = ref(null)

const selected = computed(() =>
  nodes.value.find((n) => n._uid === selectedUid.value) ?? null
)

const selectedTypeDef = computed(() =>
  selected.value && !isStep(selected.value)
    ? getFieldType(selected.value.$formkit)
    : null
)

const isMultiStep = computed(() => nodes.value.some(isStep))

const duplicateNames = computed(() => findDuplicateNames(nodes.value))

const schema = computed(() => toSchema(nodes.value))

// Same lesson as the JSON pane in step 1: FormKitSchema patches positionally,
// so the preview must remount when field identity changes. Keying on
// uid+type+name means drags, retypes and renames all remount, while cosmetic
// edits (label, help, placeholder) patch cheaply.
const previewKey = computed(() =>
  nodes.value.map((n) => `${n._uid}:${n._kind}:${n.$formkit ?? ''}:${n.name}`).join('|')
)

function addField(type) {
  const node = createNode(type)
  nodes.value = [...nodes.value, node]
  selectedUid.value = node._uid
}

function addStep() {
  // Existing steps + the implicit leading step (if any fields precede the
  // first marker) + 1 for the step being added.
  const explicitSteps = nodes.value.filter(isStep).length
  const hasImplicitLeadingStep =
    nodes.value.length > 0 && !isStep(nodes.value[0])
  const node = createStep(explicitSteps + (hasImplicitLeadingStep ? 1 : 0) + 1)

  nodes.value = [...nodes.value, node]
  selectedUid.value = node._uid
}

function removeField(uid) {
  const index = nodes.value.findIndex((n) => n._uid === uid)
  nodes.value = nodes.value.filter((n) => n._uid !== uid)

  if (selectedUid.value === uid) {
    const next = nodes.value[index] ?? nodes.value[index - 1] ?? null
    selectedUid.value = next?._uid ?? null
  }
}

function changeType(uid, nextType) {
  nodes.value = nodes.value.map((n) => (n._uid === uid ? retypeNode(n, nextType) : n))
}

function updateProp(uid, key, value) {
  nodes.value = nodes.value.map((n) => (n._uid === uid ? { ...n, [key]: value } : n))
}

/** Options are edited as "value: label" lines and stored as a flat map. */
const optionsText = ref('')

watch(
  selected,
  (node) => {
    if (!node?.options) {
      optionsText.value = ''
      return
    }
    optionsText.value = Object.entries(node.options)
      .map(([value, label]) => `${value}: ${label}`)
      .join('\n')
  },
  { immediate: true }
)

function applyOptions(uid, text) {
  const options = {}
  for (const line of text.split('\n')) {
    if (!line.trim()) continue
    const [value, ...rest] = line.split(':')
    const key = value.trim()
    if (!key) continue
    options[key] = rest.join(':').trim() || key
  }
  updateProp(uid, 'options', options)
}

const aiPrompt = ref('')
const generateState = ref({ status: 'idle', message: '' })

async function generate() {
  if (!aiPrompt.value.trim()) return

  generateState.value = { status: 'working', message: 'Generating…' }
  try {
    const res = await fetch('/api/forms/generate', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ prompt: aiPrompt.value }),
    })

    if (!res.ok) {
      const problem = await res.json().catch(() => null)
      throw new Error(problem?.detail ?? `HTTP ${res.status}`)
    }

    const result = await res.json()
    // Generated forms land in the same editable model as hand-built ones —
    // the prompt is a starting point, not a finished artifact.
    nodes.value = fromSchema(result.schema)
    selectedUid.value = nodes.value.find((n) => !isStep(n))?._uid ?? null
    formName.value = result.name ?? formName.value
    generateState.value = {
      status: 'done',
      message: `Generated ${nodes.value.filter((n) => !isStep(n)).length} fields — edit below.`,
    }
  } catch (err) {
    generateState.value = { status: 'error', message: err.message }
  }
}

async function save() {
  saveState.value = { status: 'saving', message: '' }
  try {
    const res = await fetch('/api/forms', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ name: formName.value, schema: schema.value }),
    })

    if (!res.ok) {
      const problem = await res.json().catch(() => null)
      throw new Error(problem?.detail ?? `HTTP ${res.status}`)
    }

    const saved = await res.json()
    saveState.value = { status: 'saved', message: `Saved as ${saved.id}` }
  } catch (err) {
    saveState.value = { status: 'error', message: err.message }
  }
}

function handleSubmit(data) {
  submitted.value = data
}
</script>

<template>
  <div class="builder">
    <!-- AI fast start -->
    <section class="panel ai">
      <h2>Start with AI</h2>
      <textarea
        v-model="aiPrompt"
        rows="3"
        data-testid="ai-prompt"
        placeholder="Describe the form you need — e.g. “a job application with contact details, work history and a cover letter”"
      ></textarea>
      <button
        type="button"
        class="primary"
        data-testid="ai-generate"
        :disabled="generateState.status === 'working' || !aiPrompt.trim()"
        @click="generate"
      >
        {{ generateState.status === 'working' ? 'Generating…' : 'Generate form' }}
      </button>
      <p
        v-if="generateState.message"
        :class="generateState.status === 'error' ? 'error' : 'ok'"
        data-testid="ai-status"
      >
        {{ generateState.message }}
      </p>
      <p class="hint">Replaces the current fields. Everything stays editable.</p>
    </section>

    <!-- Palette -->
    <section class="panel">
      <h2>Add field</h2>
      <div class="palette">
        <button
          v-for="field in FIELD_TYPES"
          :key="field.type"
          type="button"
          @click="addField(field.type)"
        >
          {{ field.label }}
        </button>
        <button type="button" class="step-btn" data-testid="add-step" @click="addStep">
          + Step break
        </button>
      </div>
      <p class="hint">
        Adding a step break turns the form into a multi-step form. Fields below a
        break belong to that step.
      </p>
    </section>

    <!-- Field list (drag to reorder) -->
    <section class="panel">
      <h2>Fields</h2>
      <p class="hint">Drag the handle to reorder.</p>
      <ul ref="listRef" class="field-list">
        <li
          v-for="node in nodes"
          :key="node._uid"
          :class="{ selected: node._uid === selectedUid, 'step-marker': isStep(node) }"
          :data-uid="node._uid"
          @click="selectedUid = node._uid"
        >
          <span class="drag-handle" aria-label="Drag to reorder">⠿</span>
          <span class="field-summary">
            <strong>{{ node.label || '(no label)' }}</strong>
            <small>
              {{ isStep(node) ? 'step break' : node.$formkit }} ·
              <code :class="{ dupe: duplicateNames.has(node.name) }">{{ node.name }}</code>
            </small>
          </span>
          <button
            type="button"
            class="remove"
            :aria-label="`Remove ${node.label}`"
            @click.stop="removeField(node._uid)"
          >
            ×
          </button>
        </li>
      </ul>
      <p v-if="duplicateNames.size" class="error">
        Duplicate field names: {{ [...duplicateNames].join(', ') }}. The server will
        reject this form.
      </p>
    </section>

    <!-- Property editor -->
    <section class="panel">
      <h2>Properties</h2>
      <template v-if="selected">
        <p v-if="isStep(selected)" class="hint">
          Step break — everything below it, until the next break, forms one step.
        </p>

        <label v-else>
          Type
          <select
            :value="selected.$formkit"
            @change="changeType(selected._uid, $event.target.value)"
          >
            <option v-for="f in FIELD_TYPES" :key="f.type" :value="f.type">
              {{ f.label }}
            </option>
          </select>
        </label>

        <label>
          Name
          <input
            :value="selected.name"
            data-testid="prop-name"
            @input="updateProp(selected._uid, 'name', $event.target.value)"
          />
        </label>

        <label>
          Label
          <input
            :value="selected.label"
            data-testid="prop-label"
            @input="updateProp(selected._uid, 'label', $event.target.value)"
          />
        </label>

        <label v-if="selectedTypeDef?.props.includes('placeholder')">
          Placeholder
          <input
            :value="selected.placeholder ?? ''"
            @input="updateProp(selected._uid, 'placeholder', $event.target.value)"
          />
        </label>

        <label v-if="selectedTypeDef?.props.includes('help')">
          Help text
          <input
            :value="selected.help ?? ''"
            @input="updateProp(selected._uid, 'help', $event.target.value)"
          />
        </label>

        <label v-if="selectedTypeDef?.props.includes('validation')">
          Validation
          <input
            :value="selected.validation ?? ''"
            placeholder="required|email"
            @input="updateProp(selected._uid, 'validation', $event.target.value)"
          />
        </label>

        <label v-if="selectedTypeDef?.props.includes('options')">
          Options (one per line, <code>value: label</code>)
          <textarea
            v-model="optionsText"
            rows="4"
            @input="applyOptions(selected._uid, optionsText)"
          ></textarea>
        </label>
      </template>
      <p v-else class="hint">Select a field to edit it.</p>
    </section>

    <!-- Live preview -->
    <section class="panel preview">
      <h2>Preview</h2>
      <FormKit
        v-if="nodes.length"
        :key="previewKey"
        type="form"
        @submit="handleSubmit"
      >
        <FormKitSchema :schema="schema" />
      </FormKit>
      <p v-else class="hint">Add a field to see the preview.</p>

      <template v-if="submitted">
        <h3>Submitted</h3>
        <pre data-testid="submitted">{{ submitted }}</pre>
      </template>
    </section>

    <!-- Save -->
    <section class="panel">
      <h2>Save</h2>
      <label>
        Form name
        <input v-model="formName" />
      </label>
      <button type="button" class="primary" @click="save">Save form</button>
      <p
        v-if="saveState.message"
        :class="saveState.status === 'error' ? 'error' : 'ok'"
        data-testid="save-status"
      >
        {{ saveState.message }}
      </p>
    </section>
  </div>
</template>

<style scoped>
.builder {
  display: grid;
  grid-template-columns: 20rem 1fr;
  gap: 1.5rem;
  align-items: start;
}

.preview {
  grid-row: span 4;
  grid-column: 2;
}

.ai {
  border-color: #b9cdf5;
  background: #f7faff;
}

.ai textarea {
  width: 100%;
  font: inherit;
  font-size: 0.85rem;
  padding: 0.5rem;
  border: 1px solid #ccc;
  border-radius: 4px;
  margin-bottom: 0.5rem;
  resize: vertical;
}

.ai .primary:disabled {
  opacity: 0.55;
  cursor: not-allowed;
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

.palette {
  display: flex;
  flex-wrap: wrap;
  gap: 0.4rem;
}

.palette button {
  padding: 0.3rem 0.6rem;
  font-size: 0.8rem;
  cursor: pointer;
}

.field-list {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: 0.35rem;
}

.field-list li {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  padding: 0.5rem;
  border: 1px solid #ddd;
  border-radius: 4px;
  background: #fff;
  cursor: pointer;
}

.field-list li.selected {
  border-color: #4a7;
  background: #f2fbf6;
}

.field-list li.step-marker {
  background: #eef4ff;
  border-color: #b9cdf5;
  border-left: 3px solid #4a7adf;
}

.field-list li.step-marker.selected {
  border-color: #4a7adf;
}

.palette .step-btn {
  border-color: #4a7adf;
  color: #2b56b5;
}

.drag-handle {
  cursor: grab;
  color: #999;
  user-select: none;
}

.field-summary {
  display: flex;
  flex-direction: column;
  flex: 1;
  min-width: 0;
}

.field-summary small {
  color: #777;
}

code.dupe {
  color: #b00020;
  font-weight: 700;
}

.remove {
  border: none;
  background: none;
  font-size: 1.1rem;
  cursor: pointer;
  color: #999;
}

.remove:hover {
  color: #b00020;
}

label {
  display: block;
  margin-bottom: 0.6rem;
  font-size: 0.8rem;
  color: #444;
}

label input,
label select,
label textarea {
  display: block;
  width: 100%;
  margin-top: 0.2rem;
  padding: 0.35rem;
  border: 1px solid #ccc;
  border-radius: 4px;
  font: inherit;
}

.primary {
  padding: 0.45rem 0.9rem;
  cursor: pointer;
}

.hint {
  color: #888;
  font-size: 0.8rem;
}

.error {
  color: #b00020;
  font-size: 0.8rem;
}

.ok {
  color: #2a7;
  font-size: 0.8rem;
}

pre {
  background: #f5f5f5;
  padding: 0.6rem;
  border-radius: 4px;
  overflow-x: auto;
  font-size: 0.8rem;
}
</style>
