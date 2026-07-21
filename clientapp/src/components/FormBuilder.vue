<script setup>
import { computed, ref, watch } from 'vue'
import { FormKitSchema } from '@formkit/vue'
import { useDragAndDrop } from '@formkit/drag-and-drop/vue'
import {
  COLUMN_SPANS,
  DEFAULT_COLUMN_SPAN,
  FIELD_TYPES,
  GRID_COLUMNS,
  getFieldType,
} from '../builder/fieldTypes.js'
import {
  createNode,
  createStep,
  findDuplicateNames,
  fromSchema,
  isStep,
  mergeSchema,
  retypeNode,
  toRenderSchema,
  toSchema,
} from '../builder/schemaModel.js'
import { apiUrl } from '../api.js'

// useDragAndDrop owns the array: `nodes` is reordered in place on drop.
//
// The canvas lays fields out on a wrapping grid, so drops are resolved in two
// dimensions: the library compares the dragged and hovered rects, takes
// whichever axis dominates, and applies the matching threshold. The horizontal
// threshold is raised because side-by-side cards differ in width — against a
// narrow neighbour the default trips as soon as the pointer enters the card,
// which reads as the layout twitching before the user has committed to a side.
const [listRef, nodes] = useDragAndDrop(
  [createNode('text'), createNode('email')],
  { threshold: { horizontal: 0.35, vertical: 0.15 } }
)

/**
 * The builder is a three-page flow: describe, build, review.
 *
 * Local state rather than routes — the project has no router, and adding one to
 * move between three panes of a single editor would put the in-progress form at
 * the mercy of the back button, since nothing is persisted until save.
 *
 * Navigation is never blocked. Page 1 is skippable by definition, and an empty
 * or half-built form on pages 2 and 3 says so in place; refusing to advance
 * would hide the very thing the user is trying to look at.
 */
// The pages are toggled with v-show, not v-if: useDragAndDrop binds to the canvas
// <ul> the first time it appears and then stops watching, and it only tears down
// on component unmount. A v-if would destroy that element on every navigation and
// remount one nothing rebinds to, leaving the canvas silently undraggable —
// reliably so after generating, since that path returns to Build from Describe.
const PAGES = [
  { id: 1, label: 'Describe', hint: 'Optional' },
  { id: 2, label: 'Build' },
  { id: 3, label: 'Review & save' },
]

const page = ref(1)

function goTo(id) {
  page.value = Math.min(Math.max(id, 1), PAGES.length)
}

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

// What the preview renders, and what a consumer of the saved form would render:
// the same schema with columnSpan resolved to a grid class. Saving sends
// `schema`, not this — the width stays semantic in storage.
const renderSchema = computed(() => toRenderSchema(schema.value))

/**
 * One field, rendered on the canvas exactly as the preview renders it.
 *
 * The width is deliberately dropped: the card itself carries the col-span class
 * and is the grid item, so leaving columnSpan on the inner node would apply the
 * span twice — once to the card, once to a field already filling it.
 */
function canvasSchema(node) {
  const { _uid, _kind, columnSpan, ...rest } = node
  return [rest]
}

/**
 * FormKitSchema patches positionally, so each card is keyed on the identity of
 * the field it renders. Without this a drag would leave cards showing the wrong
 * input — the same lesson the preview already learned.
 */
function canvasKey(node) {
  return `${node._uid}:${node.$formkit ?? ''}:${node.name}`
}

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
    const res = await fetch(apiUrl('/forms/generate'), {
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
      message: `Generated ${fieldCount(nodes.value)} fields.`,
    }
    // A successful generation is the end of page 1's job; carry the user to the
    // fields rather than leaving them on a prompt box that has already run.
    goTo(2)
  } catch (err) {
    generateState.value = { status: 'error', message: err.message }
  }
}

/**
 * The second prompt: an instruction that changes the form in place.
 *
 * Lives on the Build page rather than beside the first one, because the whole
 * point is watching the change land on the canvas. The two are not variants of
 * one control — page 1 answers "what form?" and replaces everything, this
 * answers "what about it?" and keeps everything it wasn't asked about.
 */
const refinePrompt = ref('')
const refineState = ref({ status: 'idle', message: '' })

function fieldCount(list) {
  return list.filter((n) => !isStep(n)).length
}

/** Field count is all we can report cheaply; an edit in place says so honestly. */
function describeRefinement(before, after) {
  const delta = after - before
  if (delta > 0) return `Added ${delta} field${delta === 1 ? '' : 's'}.`
  if (delta < 0) return `Removed ${-delta} field${delta === -1 ? '' : 's'}.`
  return 'Form updated.'
}

async function refine() {
  if (!refinePrompt.value.trim() || !nodes.value.length) return

  refineState.value = { status: 'working', message: 'Applying…' }
  const before = fieldCount(nodes.value)

  try {
    const res = await fetch(apiUrl('/forms/refine'), {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      // The current form goes up with the instruction — the server holds nothing
      // between calls, so this is the only thing that makes an edit an edit.
      body: JSON.stringify({
        prompt: refinePrompt.value,
        name: formName.value,
        schema: schema.value,
      }),
    })

    if (!res.ok) {
      const problem = await res.json().catch(() => null)
      throw new Error(problem?.detail ?? `HTTP ${res.status}`)
    }

    const result = await res.json()
    nodes.value = mergeSchema(nodes.value, result.schema)
    formName.value = result.name ?? formName.value

    // Usually a no-op thanks to the uid merge, but the selected field may have
    // been the very thing the instruction removed.
    if (!nodes.value.some((n) => n._uid === selectedUid.value)) {
      selectedUid.value = nodes.value.find((n) => !isStep(n))?._uid ?? null
    }

    // An instruction is spent once applied. Leaving it in the box invites a
    // second click that would apply "add a phone number" all over again.
    refinePrompt.value = ''
    refineState.value = {
      status: 'done',
      message: describeRefinement(before, fieldCount(nodes.value)),
    }
  } catch (err) {
    refineState.value = { status: 'error', message: err.message }
  }
}

async function save() {
  saveState.value = { status: 'saving', message: '' }
  try {
    const res = await fetch(apiUrl('/forms'), {
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
  <div class="wizard">
    <nav class="steps" aria-label="Builder steps">
      <button
        v-for="p in PAGES"
        :key="p.id"
        type="button"
        class="step-tab"
        :class="{ current: p.id === page, done: p.id < page }"
        :aria-current="p.id === page ? 'step' : undefined"
        :data-testid="`step-${p.id}`"
        @click="goTo(p.id)"
      >
        <span class="step-num">{{ p.id }}</span>
        {{ p.label }}
        <small v-if="p.hint">{{ p.hint }}</small>
      </button>
    </nav>

    <!-- Page 1: describe the form, or skip straight to building it -->
    <div v-show="page === 1" class="page page-describe">
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
        <p class="hint">
          Replaces the current fields. Everything stays editable — and to change a
          form you already have, use Refine on the Build step instead.
        </p>
      </section>

      <div class="page-nav">
        <button type="button" class="ghost" data-testid="skip-ai" @click="goTo(2)">
          Skip — build it by hand →
        </button>
      </div>
    </div>

    <!--
      Page 2. Controls on the left, the form itself on the right: the canvas
      renders the real inputs at their real widths, so it needs the wide column —
      a 12-track grid in a 20rem sidebar would collapse every distinction between
      a half and a quarter.
    -->
    <div v-show="page === 2" class="page builder">
      <div class="sidebar">
        <!-- The second prompt: edits what's on the canvas instead of replacing it -->
        <section class="panel ai">
          <h2>Refine with AI</h2>
          <textarea
            v-model="refinePrompt"
            rows="3"
            data-testid="refine-prompt"
            placeholder="Describe a change — e.g. “add a phone number beside the email” or “make the budget field optional”"
          ></textarea>
          <button
            type="button"
            class="primary"
            data-testid="refine-apply"
            :disabled="refineState.status === 'working' || !refinePrompt.trim() || !nodes.length"
            @click="refine"
          >
            {{ refineState.status === 'working' ? 'Applying…' : 'Apply change' }}
          </button>
          <p
            v-if="refineState.message"
            :class="refineState.status === 'error' ? 'error' : 'ok'"
            data-testid="refine-status"
          >
            {{ refineState.message }}
          </p>
          <p class="hint">
            <template v-if="nodes.length">
              Changes the fields below rather than replacing them. Anything the
              instruction doesn't mention is left alone.
            </template>
            <template v-else>
              Add a field first — this edits a form, it doesn't start one.
            </template>
          </p>
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

        <label v-if="!isStep(selected)">
          Width
          <select
            :value="selected.columnSpan ?? DEFAULT_COLUMN_SPAN"
            data-testid="prop-width"
            @change="updateProp(selected._uid, 'columnSpan', Number($event.target.value))"
          >
            <option v-for="option in COLUMN_SPANS" :key="option.span" :value="option.span">
              {{ option.label }}
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

      </div>

      <div class="main">
      <!-- Layout canvas: the form as it will render, rearranged by dragging -->
      <section class="panel canvas-panel">
        <h2>Fields</h2>
        <p class="hint">
          Drag a field to move it. Widths are set under Properties; a field that
          no longer fits its row wraps onto the next one.
        </p>
        <ul ref="listRef" class="field-canvas">
          <li
            v-for="node in nodes"
            :key="node._uid"
            :class="[
              'canvas-card',
              isStep(node)
                ? 'col-span-12'
                : `col-span-${node.columnSpan ?? DEFAULT_COLUMN_SPAN}`,
              {
                selected: node._uid === selectedUid,
                'step-marker': isStep(node),
                'dupe-name': duplicateNames.has(node.name),
              },
            ]"
            :data-uid="node._uid"
            @click="selectedUid = node._uid"
          >
            <span v-if="isStep(node)" class="step-rule">
              Step break — {{ node.label }} <code>{{ node.name }}</code>
            </span>
            <FormKitSchema v-else :key="canvasKey(node)" :schema="canvasSchema(node)" />

            <!--
              Covers the rendered field so the card reads as one draggable
              object: without it a pointer down on an input would focus it
              instead of starting a drag, and a select would swallow the
              gesture entirely.
            -->
            <span class="canvas-shield"></span>

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
        <p v-if="!nodes.length" class="hint">Add a field to start laying out.</p>
        <p v-if="duplicateNames.size" class="error">
          Duplicate field names: {{ [...duplicateNames].join(', ') }}. The server
          will reject this form.
        </p>
      </section>
      </div>

      <div class="page-nav">
        <button type="button" class="ghost" @click="goTo(1)">← Back</button>
        <button type="button" class="primary" data-testid="to-review" @click="goTo(3)">
          Review &amp; save →
        </button>
      </div>
    </div>

    <!-- Page 3: the real, interactive, submittable form, then save -->
    <div v-show="page === 3" class="page page-review">
      <section class="panel preview">
        <h2>Preview</h2>
        <FormKit
          v-if="nodes.length"
          :key="previewKey"
          type="form"
          @submit="handleSubmit"
        >
          <FormKitSchema :schema="renderSchema" />
        </FormKit>
        <p v-else class="hint">
          No fields yet — go back to Build and add some.
        </p>

        <template v-if="submitted">
          <h3>Submitted</h3>
          <pre data-testid="submitted">{{ submitted }}</pre>
        </template>
      </section>

      <section class="panel">
        <h2>Save</h2>
        <label>
          Form name
          <input v-model="formName" />
        </label>
        <!--
          Duplicate names are surfaced here as well as on the canvas: this is the
          last screen before the save the server would reject, and the field
          causing it is no longer on screen to show inline.
        -->
        <p v-if="duplicateNames.size" class="error">
          Duplicate field names: {{ [...duplicateNames].join(', ') }}. Fix these on
          the Build step before saving.
        </p>
        <button
          type="button"
          class="primary"
          :disabled="!nodes.length || saveState.status === 'saving'"
          @click="save"
        >
          {{ saveState.status === 'saving' ? 'Saving…' : 'Save form' }}
        </button>
        <p
          v-if="saveState.message"
          :class="saveState.status === 'error' ? 'error' : 'ok'"
          data-testid="save-status"
        >
          {{ saveState.message }}
        </p>
      </section>

      <div class="page-nav">
        <button type="button" class="ghost" @click="goTo(2)">← Back to build</button>
      </div>
    </div>
  </div>
</template>

<style scoped>
.wizard {
  display: flex;
  flex-direction: column;
  gap: 1.5rem;
}

.steps {
  display: flex;
  gap: 0.5rem;
  flex-wrap: wrap;
}

.step-tab {
  display: flex;
  align-items: baseline;
  gap: 0.5rem;
  padding: 0.5rem 0.9rem;
  font: inherit;
  font-size: 0.85rem;
  color: #666;
  background: #fff;
  border: 1px solid #ddd;
  border-radius: 6px;
  cursor: pointer;
}

.step-tab small {
  font-size: 0.7rem;
  color: #999;
}

.step-tab.current {
  border-color: #4a7adf;
  color: #2b56b5;
  background: #f7faff;
}

/* Visited steps stay reachable — nothing here is a commitment. */
.step-tab.done .step-num {
  background: #4a7;
}

.step-num {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 1.35rem;
  height: 1.35rem;
  border-radius: 50%;
  font-size: 0.75rem;
  color: #fff;
  background: #bbb;
}

.step-tab.current .step-num {
  background: #4a7adf;
}

.page-describe,
.page-review {
  display: flex;
  flex-direction: column;
  gap: 1.5rem;
  max-width: 46rem;
}

.page-nav {
  display: flex;
  justify-content: space-between;
  gap: 0.75rem;
}

/* Sits below both columns of the builder grid rather than inside one. */
.builder .page-nav {
  grid-column: 1 / -1;
}

.ghost {
  padding: 0.45rem 0.9rem;
  font: inherit;
  font-size: 0.85rem;
  color: #555;
  background: none;
  border: 1px solid #ccc;
  border-radius: 4px;
  cursor: pointer;
}

.builder {
  display: grid;
  grid-template-columns: 20rem minmax(0, 1fr);
  gap: 1.5rem;
  align-items: start;
}

/*
 * minmax(0, 1fr) above rather than 1fr: the canvas is a grid whose tracks are
 * sized from the column, and a bare 1fr floors at min-content, so a long field
 * label would widen the column instead of wrapping.
 */
.sidebar,
.main {
  display: flex;
  flex-direction: column;
  gap: 1.5rem;
  min-width: 0;
}

@media (max-width: 900px) {
  .builder {
    grid-template-columns: minmax(0, 1fr);
  }
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

.primary:disabled {
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

.palette .step-btn {
  border-color: #4a7adf;
  color: #2b56b5;
}

/*
 * The canvas grid itself lives in the global stylesheet, alongside the identical
 * rules for .formkit-form and .formkit-step-inner — one definition for all three
 * containers is what keeps the canvas and the preview honestly in step.
 */
.field-canvas {
  list-style: none;
  margin: 0;
  padding: 0;
  min-height: 3rem;
}

.canvas-card {
  position: relative;
  padding: 0.5rem 1.5rem 0.5rem 0.6rem;
  border: 1px dashed transparent;
  border-radius: 4px;
  cursor: grab;
}

.canvas-card:hover {
  border-color: #cfd6e4;
  background: #fafbfd;
}

.canvas-card.selected {
  border-color: #4a7;
  border-style: solid;
  background: #f2fbf6;
}

.canvas-card.dupe-name {
  border-color: #b00020;
  border-style: solid;
}

/*
 * The library clones the card while dragging; keeping the cursor grabbing here
 * stops the pointer flicking back to the arrow mid-drag.
 */
.canvas-card:active {
  cursor: grabbing;
}

.canvas-card.step-marker {
  padding: 0;
}

.step-rule {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  padding: 0.35rem 0.6rem;
  font-size: 0.75rem;
  color: #2b56b5;
  background: #eef4ff;
  border: 1px solid #b9cdf5;
  border-left: 3px solid #4a7adf;
  border-radius: 4px;
}

/*
 * Transparent and on top of the card's contents. The inputs below still render
 * exactly as they will in the form, but never take focus or a click.
 */
.canvas-shield {
  position: absolute;
  inset: 0;
  z-index: 1;
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

/* Above the shield, or it could not be clicked. */
.canvas-card .remove {
  position: absolute;
  top: 0.25rem;
  right: 0.25rem;
  z-index: 2;
  line-height: 1;
  opacity: 0;
}

.canvas-card:hover .remove,
.canvas-card.selected .remove {
  opacity: 1;
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
