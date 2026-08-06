<script setup>
import { ref, watch, onMounted, computed } from 'vue'
import { useEditor, EditorContent } from '@tiptap/vue-3'
import StarterKit from '@tiptap/starter-kit'
import { apiUrl } from '../api.js'
import { MergeField } from '../report/mergeFieldExtension.js'
import { BlockRef } from '../report/blockRefExtension.js'
import { deriveFields, buildMockData } from '../report/mockData.js'
import { renderTemplate, collectBlockRefIds } from '../report/renderTemplate.js'

/**
 * The report builder is the same three-page flow as the form builder: describe,
 * build, review & save. See the note in FormBuilder.vue for why this is local
 * state rather than routes, and why the pages are toggled with v-show — the
 * TipTap editor binds to its host element once and is torn down only on unmount,
 * so a v-if that destroyed and recreated the Build page would leave a dead
 * editor behind (the same hazard the form builder's drag-and-drop canvas has).
 */
const PAGES = [
  { id: 1, label: 'Describe', hint: 'Optional' },
  { id: 2, label: 'Build' },
  { id: 3, label: 'Review & save' },
]

const page = ref(1)

function goTo(id) {
  page.value = Math.min(Math.max(id, 1), PAGES.length)
}

// --- state -----------------------------------------------------------------

const reportName = ref('Untitled report')
const forms = ref([]) // {id, name} from GET /api/forms
const selectedFormId = ref('') // which form this report binds to
const fields = ref([]) // merge-field candidates for the selected form
const mockData = ref({}) // sample values, keyed like a real submission

const submissions = ref([]) // captured responses for the selected form
const selectedSubmissionId = ref('') // '' = preview against sample data

// The chosen response, or null for sample data. Its `data` is already the flat
// merge-key map the renderer consumes — the same shape as mockData — so switching
// between them needs no reshaping.
const selectedSubmission = computed(() =>
  submissions.value.find((s) => s.id === selectedSubmissionId.value) ?? null
)

// What the preview and PDF actually render against: a real captured response when
// one is chosen, otherwise the sample data (the original behaviour, kept as a
// fallback so a report with no responses yet still previews).
const previewData = computed(() =>
  selectedSubmission.value ? selectedSubmission.value.data : mockData.value
)

const previewSource = computed(() =>
  selectedSubmission.value ? `captured ${formatWhen(selectedSubmission.value.createdAt)}` : 'sample data'
)

function formatWhen(iso) {
  try {
    return new Date(iso).toLocaleString()
  } catch {
    return iso
  }
}

const templates = ref([]) // saved report templates
const currentTemplateId = ref(null) // set once saved, so save becomes an update
const openTemplateId = ref('') // bound to the "Open existing" picker

// Reusable content blocks (snippets, headers, footers). Inserting one drops a
// live reference (a blockRef node) that transcludes the block at render time, so
// editing the block updates every report referencing it. `blockMap` holds the
// resolved content of the blocks the current document references, keyed by id —
// what renderTemplate reads to draw them.
const blocks = ref([]) // {id, name, kind, formId} from GET /api/blocks
const blockMap = ref({}) // id -> block `content` (doc), for referenced blocks
const insertBlockId = ref('') // bound to the insert-snippet picker
const snippetName = ref('') // name for a snippet being saved
const snippetKind = ref('snippet') // header | footer | snippet
const snippetState = ref({ status: 'idle', message: '' })

const SNIPPET_KINDS = [
  { value: 'header', label: 'Header' },
  { value: 'footer', label: 'Footer' },
  { value: 'snippet', label: 'Snippet' },
]

function kindLabel(kind) {
  return SNIPPET_KINDS.find((k) => k.value === kind)?.label ?? kind
}

const insertIndex = ref('') // bound to the insert-field picker
const buildPreviewRef = ref(null)
const reviewPreviewRef = ref(null)
const docJson = ref(null) // latest editor doc, drives the preview

const saveState = ref({ status: 'idle', message: '' })

const hasForm = computed(() => Boolean(selectedFormId.value))

// --- editor ----------------------------------------------------------------

// useEditor stores the instance in a shallowRef and destroys it on unmount, so
// Vue's reactivity never proxies ProseMirror's internal state.
const editor = useEditor({
  extensions: [StarterKit.configure({ heading: { levels: [1, 2, 3] } }), MergeField, BlockRef],
  content: emptyDoc(),
  onCreate: ({ editor }) => {
    docJson.value = editor.getJSON()
  },
  onUpdate: ({ editor }) => {
    docJson.value = editor.getJSON()
  },
})

function emptyDoc() {
  return { type: 'doc', content: [{ type: 'paragraph' }] }
}

// --- data loading ----------------------------------------------------------

onMounted(async () => {
  await Promise.all([loadForms(), loadTemplates(), loadBlocks()])
})

async function loadForms() {
  try {
    const res = await fetch(apiUrl('/forms'))
    if (!res.ok) throw new Error(`HTTP ${res.status}`)
    forms.value = await res.json()
  } catch {
    forms.value = []
  }
}

async function loadTemplates() {
  try {
    const res = await fetch(apiUrl('/report-templates'))
    if (!res.ok) throw new Error(`HTTP ${res.status}`)
    templates.value = await res.json()
  } catch {
    templates.value = []
  }
}

async function loadBlocks() {
  try {
    const res = await fetch(apiUrl('/blocks'))
    if (!res.ok) throw new Error(`HTTP ${res.status}`)
    blocks.value = await res.json()
  } catch {
    blocks.value = []
  }
}

// When the bound form changes, refresh the merge-field list and sample data.
// The editor content is left alone: any field that no longer exists simply
// renders a [missing] marker in the preview.
watch(selectedFormId, async (id) => {
  // A form change invalidates the previously chosen response — its fields, and so
  // its merge keys, belong to the old form.
  selectedSubmissionId.value = ''
  submissions.value = []

  if (!id) {
    fields.value = []
    mockData.value = {}
    return
  }

  try {
    const [formRes, subsRes] = await Promise.all([
      fetch(apiUrl(`/forms/${id}`)),
      fetch(apiUrl(`/forms/${id}/submissions`)),
    ])
    if (!formRes.ok) throw new Error(`HTTP ${formRes.status}`)
    const form = await formRes.json()
    fields.value = deriveFields(form.schema)
    mockData.value = buildMockData(fields.value)
    submissions.value = subsRes.ok ? await subsRes.json() : []
  } catch {
    fields.value = []
    mockData.value = {}
    submissions.value = []
  }
})

// --- preview ---------------------------------------------------------------

// Rebuild the preview whenever the document, the data it's filled with, or the
// resolved snippet content changes. Both the Build and Review pages carry a
// preview host; each gets its own rendered copy, since a single element can't be
// appended to two parents.
watch([docJson, previewData, blockMap], renderPreview, { deep: false })

// When the document changes, make sure every snippet it references is resolved.
// blockMap updating retriggers the preview watch above, so a freshly inserted
// reference fills in as soon as its block is fetched.
watch(docJson, (doc) => resolveBlockRefs(doc))

function renderPreview() {
  for (const host of [buildPreviewRef.value, reviewPreviewRef.value]) {
    if (host) host.replaceChildren(renderTemplate(docJson.value, previewData.value, blockMap.value))
  }
}

// Fetch the content of any referenced block not already in blockMap. Only the
// newly-referenced ids are fetched, so typing (which doesn't change the id set)
// costs nothing; a reference whose block can't be fetched is left unresolved and
// renders a [missing snippet] marker until it resolves.
async function resolveBlockRefs(doc) {
  const ids = collectBlockRefIds(doc)
  const missing = [...ids].filter((id) => !(id in blockMap.value))
  if (!missing.length) return

  const additions = {}
  await Promise.all(
    missing.map(async (id) => {
      try {
        const res = await fetch(apiUrl(`/blocks/${id}`))
        if (res.ok) additions[id] = (await res.json()).content
      } catch {
        // Leave unresolved; the preview shows a missing-snippet marker.
      }
    })
  )

  if (Object.keys(additions).length) {
    blockMap.value = { ...blockMap.value, ...additions }
  }
}

// --- AI generation ---------------------------------------------------------

const aiPrompt = ref('')
const generateState = ref({ status: 'idle', message: '' })

// A photo/screenshot or PDF of an existing report or letter to transcribe. Held
// as { name, mediaType, data } where data is base64 with no data-URL prefix —
// the shape the server's GenerateReportRequest expects. Caps mirror the server.
const aiFile = ref(null)
const fileInputRef = ref(null)
const ACCEPTED_FILES = {
  'image/png': 5,
  'image/jpeg': 5,
  'image/gif': 5,
  'image/webp': 5,
  'application/pdf': 20,
}

// A report merges in a form's fields, so it can't be generated without knowing
// which form — the prompt or attachment alone isn't enough.
const canGenerate = computed(() => hasForm.value && (!!aiPrompt.value.trim() || !!aiFile.value))

function readFileAsBase64(file) {
  return new Promise((resolve, reject) => {
    const reader = new FileReader()
    reader.onload = () => {
      const result = String(reader.result)
      // Drop the "data:<mime>;base64," prefix; the server wants raw base64.
      resolve(result.slice(result.indexOf(',') + 1))
    }
    reader.onerror = () => reject(reader.error ?? new Error('Could not read that file.'))
    reader.readAsDataURL(file)
  })
}

async function onFileChange(event) {
  const file = event.target.files?.[0]
  if (!file) return

  const maxMb = ACCEPTED_FILES[file.type]
  if (!maxMb) {
    clearFile()
    generateState.value = {
      status: 'error',
      message: 'Attach a PNG, JPEG, GIF or WebP image, or a PDF.',
    }
    return
  }
  if (file.size > maxMb * 1024 * 1024) {
    clearFile()
    const kind = file.type === 'application/pdf' ? 'PDF' : 'image'
    generateState.value = { status: 'error', message: `That ${kind} is over ${maxMb} MB.` }
    return
  }

  try {
    const data = await readFileAsBase64(file)
    aiFile.value = { name: file.name, mediaType: file.type, data }
    generateState.value = { status: 'idle', message: '' }
  } catch (err) {
    clearFile()
    generateState.value = { status: 'error', message: err.message }
  }
}

function clearFile() {
  aiFile.value = null
  if (fileInputRef.value) fileInputRef.value.value = ''
}

async function generate() {
  if (!canGenerate.value || !editor.value) return

  generateState.value = { status: 'working', message: 'Generating…' }
  try {
    const res = await fetch(apiUrl('/report-templates/generate'), {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        prompt: aiPrompt.value,
        formId: selectedFormId.value,
        fileData: aiFile.value?.data ?? null,
        fileMediaType: aiFile.value?.mediaType ?? null,
      }),
    })

    if (!res.ok) {
      const problem = await res.json().catch(() => null)
      throw new Error(problem?.detail ?? `HTTP ${res.status}`)
    }

    const result = await res.json()
    // A generated template lands in the same editable document as a hand-built
    // one — the prompt is a starting point, not a finished artifact. It's a new,
    // unsaved report, so clear any prior template identity.
    currentTemplateId.value = null
    openTemplateId.value = ''
    reportName.value = result.name ?? reportName.value
    editor.value.commands.setContent(result.content)
    docJson.value = editor.value.getJSON()
    generateState.value = { status: 'done', message: 'Generated. Everything stays editable.' }
    saveState.value = { status: 'idle', message: '' }
    // A successful generation is the end of the Describe page's job; carry the
    // user to the editor rather than leaving them on a prompt that has run.
    goTo(2)
  } catch (err) {
    generateState.value = { status: 'error', message: err.message }
  }
}

// --- AI refinement ---------------------------------------------------------

const refinePrompt = ref('')
const refineState = ref({ status: 'idle', message: '' })

async function refine() {
  if (!refinePrompt.value.trim() || !hasForm.value || !editor.value) return

  refineState.value = { status: 'working', message: 'Applying…' }
  try {
    const res = await fetch(apiUrl('/report-templates/refine'), {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      // The current template goes up with the instruction — the server holds
      // nothing between calls, so this is what makes an edit an edit. Unlike a
      // generation this keeps currentTemplateId, so a refined saved report still
      // updates on save rather than forking a copy.
      body: JSON.stringify({
        prompt: refinePrompt.value,
        name: reportName.value,
        formId: selectedFormId.value,
        content: editor.value.getJSON(),
      }),
    })

    if (!res.ok) {
      const problem = await res.json().catch(() => null)
      throw new Error(problem?.detail ?? `HTTP ${res.status}`)
    }

    const result = await res.json()
    reportName.value = result.name ?? reportName.value
    editor.value.commands.setContent(result.content)
    docJson.value = editor.value.getJSON()

    // An instruction is spent once applied. Leaving it in the box invites a
    // second click that would apply the same change all over again.
    refinePrompt.value = ''
    refineState.value = { status: 'done', message: 'Applied. Everything stays editable.' }
  } catch (err) {
    refineState.value = { status: 'error', message: err.message }
  }
}

// --- toolbar actions -------------------------------------------------------

const chain = () => editor.value?.chain().focus()

function insertSelectedField() {
  const idx = Number(insertIndex.value)
  const field = fields.value[idx]
  insertIndex.value = ''
  if (!field || !editor.value) return
  chain()
    .insertMergeField({ name: field.name, label: field.label, step: field.step })
    .run()
}

// --- reusable blocks (snippets) --------------------------------------------

// Insert a live reference to a saved block at the cursor. The document stores only
// the block's id (plus its name, cached for the editor chip); the content is
// resolved and rendered in the preview via blockMap, and re-editing the block
// later changes this report too. Use Detach (on the chip) to turn a reference into
// an editable copy. The block's own merge fields resolve against whatever form
// this report is on, a [missing] marker if it was authored against other fields.
function insertSelectedBlock() {
  const id = insertBlockId.value
  insertBlockId.value = ''
  if (!id || !editor.value) return

  const block = blocks.value.find((b) => b.id === id)
  chain().insertBlockRef({ id, name: block?.name ?? null }).run()
  snippetState.value = { status: 'idle', message: '' }
}

// The document to save as a snippet: the current selection if there is one (so a
// client-info header can be lifted out of a larger report), otherwise the whole
// document. Wrapped as a `doc` either way, which is what the server validates and
// what insert reads back.
function snippetContent() {
  const { selection } = editor.value.state
  if (selection.empty) return editor.value.getJSON()
  const nodes = selection.content().content.toJSON() ?? []
  return nodes.length ? { type: 'doc', content: nodes } : editor.value.getJSON()
}

async function saveAsSnippet() {
  if (!editor.value) return

  const name = snippetName.value.trim()
  if (!name) {
    snippetState.value = { status: 'error', message: 'Name the snippet first.' }
    return
  }

  snippetState.value = { status: 'saving', message: '' }
  try {
    const res = await fetch(apiUrl('/blocks'), {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        name,
        kind: snippetKind.value,
        // Form-scoped when the report is on a form, so the block carries the field
        // vocabulary its merge fields bind to; null (form-agnostic) otherwise.
        formId: selectedFormId.value || null,
        content: snippetContent(),
      }),
    })

    if (!res.ok) {
      const problem = await res.json().catch(() => null)
      throw new Error(problem?.detail ?? `HTTP ${res.status}`)
    }

    snippetName.value = ''
    snippetState.value = { status: 'saved', message: 'Saved to the snippet library.' }
    await loadBlocks()
  } catch (err) {
    snippetState.value = { status: 'error', message: err.message }
  }
}

// --- persistence -----------------------------------------------------------

function newReport() {
  currentTemplateId.value = null
  openTemplateId.value = ''
  reportName.value = 'Untitled report'
  editor.value?.commands.setContent(emptyDoc())
  docJson.value = editor.value?.getJSON()
  saveState.value = { status: 'idle', message: '' }
  generateState.value = { status: 'idle', message: '' }
  aiPrompt.value = ''
  clearFile()
}

async function openTemplate() {
  const id = openTemplateId.value
  if (!id) return
  try {
    const res = await fetch(apiUrl(`/report-templates/${id}`))
    if (!res.ok) throw new Error(`HTTP ${res.status}`)
    const tpl = await res.json()
    reportName.value = tpl.name
    currentTemplateId.value = tpl.id
    selectedFormId.value = tpl.formId // triggers the field-list reload
    editor.value?.commands.setContent(tpl.content)
    docJson.value = editor.value?.getJSON()
    saveState.value = { status: 'idle', message: '' }
    // Loaded templates skip straight to editing, like a generation does.
    goTo(2)
  } catch (err) {
    saveState.value = { status: 'error', message: err.message }
  }
}

async function save() {
  if (!editor.value) return
  if (!hasForm.value) {
    saveState.value = { status: 'error', message: 'Choose a form for this report first.' }
    return
  }

  saveState.value = { status: 'saving', message: '' }

  const body = JSON.stringify({
    name: reportName.value,
    formId: selectedFormId.value,
    content: editor.value.getJSON(),
  })

  const isUpdate = Boolean(currentTemplateId.value)
  const url = isUpdate
    ? apiUrl(`/report-templates/${currentTemplateId.value}`)
    : apiUrl('/report-templates')

  try {
    const res = await fetch(url, {
      method: isUpdate ? 'PUT' : 'POST',
      headers: { 'Content-Type': 'application/json' },
      body,
    })

    if (!res.ok) {
      const problem = await res.json().catch(() => null)
      throw new Error(problem?.detail ?? `HTTP ${res.status}`)
    }

    const saved = await res.json()
    currentTemplateId.value = saved.id
    saveState.value = { status: 'saved', message: `Saved as ${saved.id}` }
    await loadTemplates()
  } catch (err) {
    saveState.value = { status: 'error', message: err.message }
  }
}

// --- PDF export ------------------------------------------------------------

async function downloadPdf() {
  // Render a fresh copy off-screen so preview styling (borders, chips) doesn't
  // bleed into the document. html2pdf pulls in html2canvas + jsPDF, so it's
  // loaded on demand rather than in the main bundle.
  const element = renderTemplate(docJson.value, previewData.value, blockMap.value)
  element.classList.add('pdf-page')

  const { default: html2pdf } = await import('html2pdf.js')
  await html2pdf()
    .set({
      margin: 12,
      filename: `${(reportName.value || 'report').replace(/[^\w.-]+/g, '_')}.pdf`,
      html2canvas: { scale: 2 },
      jsPDF: { unit: 'mm', format: 'a4', orientation: 'portrait' },
    })
    .from(element)
    .save()
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

    <!-- Page 1: describe the report, or skip straight to building it -->
    <div v-show="page === 1" class="page page-describe">
      <section class="panel ai">
        <h2>Start with AI</h2>

        <!-- A report merges a form's fields, so the form is chosen up front:
             it's what the generator is given as the merge-field vocabulary. -->
        <label class="field">
          <span>Report on form</span>
          <select v-model="selectedFormId" data-testid="form-picker">
            <option value="" disabled>Choose a form…</option>
            <option v-for="f in forms" :key="f.id" :value="f.id">{{ f.name }}</option>
          </select>
        </label>

        <textarea
          v-model="aiPrompt"
          rows="3"
          data-testid="ai-prompt"
          placeholder="Describe the report you need — e.g. “a thank-you letter summarising the applicant's contact details and work history”"
        ></textarea>

        <!--
          Or hand the model a picture/PDF of an existing report to transcribe.
          The two inputs combine: an attachment alone works, and a prompt
          alongside it refines the result ("recreate this letter but drop the
          salary line").
        -->
        <div class="ai-attach">
          <label class="file-label">
            <input
              ref="fileInputRef"
              type="file"
              accept="image/png,image/jpeg,image/gif,image/webp,application/pdf"
              data-testid="ai-file"
              @change="onFileChange"
            />
            <span>Or attach a report (image or PDF)</span>
          </label>
          <p v-if="aiFile" class="file-chip" data-testid="ai-file-name">
            {{ aiFile.name }}
            <button type="button" class="file-clear" aria-label="Remove attachment" @click="clearFile">
              ×
            </button>
          </p>
        </div>

        <button
          type="button"
          class="primary"
          data-testid="ai-generate"
          :disabled="generateState.status === 'working' || !canGenerate"
          @click="generate"
        >
          {{ generateState.status === 'working' ? 'Generating…' : 'Generate report' }}
        </button>
        <p
          v-if="generateState.message"
          :class="generateState.status === 'error' ? 'error' : 'ok'"
          data-testid="ai-status"
        >
          {{ generateState.message }}
        </p>
        <p v-else-if="!hasForm" class="hint">Choose a form to enable generation.</p>
        <p class="hint">Replaces the current template. Everything stays editable.</p>
      </section>

      <!-- Alternative starting points: reopen a saved report, or start blank. -->
      <section class="panel">
        <h2>Or open a saved report</h2>
        <label class="field">
          <span>Saved reports</span>
          <select v-model="openTemplateId" data-testid="open-template" @change="openTemplate">
            <option value="">New report…</option>
            <option v-for="t in templates" :key="t.id" :value="t.id">{{ t.name }}</option>
          </select>
        </label>
      </section>

      <div class="page-nav">
        <button type="button" class="ghost" data-testid="skip-ai" @click="goTo(2)">
          Skip — build it by hand →
        </button>
      </div>
    </div>

    <!-- Page 2: the WYSIWYG editor with a live, sample-filled preview beside it -->
    <div v-show="page === 2" class="page page-build">
      <label class="field build-form">
        <span>Reports on form</span>
        <select v-model="selectedFormId" data-testid="form-picker-build">
          <option value="" disabled>Choose a form…</option>
          <option v-for="f in forms" :key="f.id" :value="f.id">{{ f.name }}</option>
        </select>
        <small v-if="!hasForm" class="hint">Choose a form to insert its fields.</small>
      </label>

      <!-- The second prompt: edits the document in the editor instead of replacing it -->
      <section class="panel ai refine">
        <h2>Refine with AI</h2>
        <textarea
          v-model="refinePrompt"
          rows="2"
          data-testid="refine-prompt"
          placeholder="Describe a change — e.g. “add a closing signature” or “mention the applicant's start date”"
        ></textarea>
        <button
          type="button"
          class="primary"
          data-testid="refine-apply"
          :disabled="refineState.status === 'working' || !refinePrompt.trim() || !hasForm"
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
        <p v-else-if="!hasForm" class="hint">Choose a form to enable refinement.</p>
        <p class="hint">
          Edits the document below rather than replacing it. Anything the
          instruction doesn't mention is left alone.
        </p>
      </section>

      <!-- Reusable blocks: save the current selection/document, or drop a saved one in -->
      <section class="panel snippets">
        <h2>Snippets</h2>
        <div class="snippet-controls">
          <label class="field snippet-insert">
            <span>Insert a saved snippet</span>
            <select
              v-model="insertBlockId"
              data-testid="insert-block"
              :disabled="blocks.length === 0"
              :title="blocks.length ? 'Insert a saved snippet at the cursor' : 'No snippets saved yet'"
              @change="insertSelectedBlock"
            >
              <option value="" disabled>
                {{ blocks.length ? 'Insert snippet…' : 'No snippets saved yet' }}
              </option>
              <option v-for="b in blocks" :key="b.id" :value="b.id">
                {{ kindLabel(b.kind) }} · {{ b.name }}
              </option>
            </select>
          </label>

          <div class="snippet-save">
            <label class="field snippet-name">
              <span>Save as snippet</span>
              <input
                v-model="snippetName"
                type="text"
                placeholder="e.g. Client information header"
                data-testid="snippet-name"
              />
            </label>
            <label class="field snippet-kind">
              <span>Kind</span>
              <select v-model="snippetKind" data-testid="snippet-kind">
                <option v-for="k in SNIPPET_KINDS" :key="k.value" :value="k.value">{{ k.label }}</option>
              </select>
            </label>
            <button
              type="button"
              class="primary"
              data-testid="save-snippet"
              :disabled="snippetState.status === 'saving'"
              @click="saveAsSnippet"
            >
              {{ snippetState.status === 'saving' ? 'Saving…' : 'Save' }}
            </button>
          </div>
        </div>

        <p
          v-if="snippetState.message"
          :class="snippetState.status === 'error' ? 'error' : 'ok'"
          data-testid="snippet-status"
        >
          {{ snippetState.message }}
        </p>
        <p class="hint">
          Select part of the document to save just that (e.g. the client-info
          header), or save the whole document. Insert drops a live reference —
          editing the snippet later updates every report that references it. Use
          <em>Detach</em> on a reference to turn it into an editable copy.
        </p>
      </section>

      <div class="workspace">
        <section class="editor-pane">
          <div class="toolbar" role="toolbar" aria-label="Formatting">
            <button
              type="button"
              class="tb"
              :class="{ on: editor?.isActive('bold') }"
              title="Bold"
              @click="chain().toggleBold().run()"
            >
              <strong>B</strong>
            </button>
            <button
              type="button"
              class="tb"
              :class="{ on: editor?.isActive('italic') }"
              title="Italic"
              @click="chain().toggleItalic().run()"
            >
              <em>I</em>
            </button>
            <button
              type="button"
              class="tb"
              :class="{ on: editor?.isActive('strike') }"
              title="Strikethrough"
              @click="chain().toggleStrike().run()"
            >
              <s>S</s>
            </button>

            <span class="tb-sep" aria-hidden="true"></span>

            <button
              v-for="lvl in [1, 2, 3]"
              :key="lvl"
              type="button"
              class="tb"
              :class="{ on: editor?.isActive('heading', { level: lvl }) }"
              :title="`Heading ${lvl}`"
              @click="chain().toggleHeading({ level: lvl }).run()"
            >
              H{{ lvl }}
            </button>

            <span class="tb-sep" aria-hidden="true"></span>

            <button
              type="button"
              class="tb"
              :class="{ on: editor?.isActive('bulletList') }"
              title="Bullet list"
              @click="chain().toggleBulletList().run()"
            >
              • List
            </button>
            <button
              type="button"
              class="tb"
              :class="{ on: editor?.isActive('orderedList') }"
              title="Numbered list"
              @click="chain().toggleOrderedList().run()"
            >
              1. List
            </button>

            <span class="tb-spacer"></span>

            <!-- Merge fields come from the selected form's schema -->
            <select
              v-model="insertIndex"
              class="insert-field"
              data-testid="insert-field"
              :disabled="!hasForm || fields.length === 0"
              :title="hasForm ? 'Insert a form field' : 'Choose a form first'"
              @change="insertSelectedField"
            >
              <option value="" disabled>
                {{ hasForm ? 'Insert field…' : 'Choose a form first' }}
              </option>
              <option v-for="(f, i) in fields" :key="`${f.step}.${f.name}`" :value="i">
                {{ f.step ? `${f.step} · ${f.label}` : f.label }}
              </option>
            </select>
          </div>

          <EditorContent :editor="editor" class="editor" data-testid="report-editor" />
        </section>

        <section class="preview-pane">
          <header class="preview-head">
            <h3>Preview</h3>
            <label class="preview-source">
              <span>Filled with</span>
              <select
                v-model="selectedSubmissionId"
                data-testid="submission-picker"
                :disabled="!hasForm"
                :title="hasForm ? 'Choose which data to preview' : 'Choose a form first'"
              >
                <option value="">Sample data</option>
                <option v-for="s in submissions" :key="s.id" :value="s.id">
                  Captured {{ formatWhen(s.createdAt) }}
                </option>
              </select>
            </label>
          </header>
          <div ref="buildPreviewRef" class="preview" data-testid="report-preview"></div>
        </section>
      </div>

      <div class="page-nav">
        <button type="button" class="ghost" @click="goTo(1)">← Back</button>
        <button type="button" class="primary" data-testid="to-review" @click="goTo(3)">
          Review &amp; save →
        </button>
      </div>
    </div>

    <!-- Page 3: final preview, then name, save and export -->
    <div v-show="page === 3" class="page page-review">
      <section class="panel preview-pane">
        <header class="preview-head">
          <h3>Preview</h3>
          <label class="preview-source">
            <span>Filled with</span>
            <select
              v-model="selectedSubmissionId"
              data-testid="submission-picker-review"
              :disabled="!hasForm"
            >
              <option value="">Sample data</option>
              <option v-for="s in submissions" :key="s.id" :value="s.id">
                Captured {{ formatWhen(s.createdAt) }}
              </option>
            </select>
          </label>
        </header>
        <div ref="reviewPreviewRef" class="preview" data-testid="report-preview-review"></div>
      </section>

      <section class="panel">
        <h2>Save</h2>
        <label class="field">
          <span>Report name</span>
          <input v-model="reportName" type="text" data-testid="report-name" />
        </label>
        <p v-if="!hasForm" class="error">Choose a form on the Describe or Build step before saving.</p>
        <div class="control-actions">
          <button
            type="button"
            class="primary"
            data-testid="save-report"
            :disabled="!hasForm || saveState.status === 'saving'"
            @click="save"
          >
            {{ saveState.status === 'saving' ? 'Saving…' : currentTemplateId ? 'Update' : 'Save' }}
          </button>
          <button type="button" class="ghost" data-testid="download-pdf" @click="downloadPdf">
            Download PDF
          </button>
          <span
            v-if="saveState.message"
            class="save-status"
            :class="saveState.status === 'error' ? 'error' : 'ok'"
            data-testid="save-status"
          >
            {{ saveState.message }}
          </span>
        </div>
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

.ghost {
  padding: 0.45rem 0.9rem;
  font: inherit;
  font-size: 0.85rem;
  color: #555;
  background: #fff;
  border: 1px solid #ccc;
  border-radius: 4px;
  cursor: pointer;
}

.ghost:hover {
  background: #f4f4f4;
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

.field {
  display: flex;
  flex-direction: column;
  gap: 0.25rem;
  margin-bottom: 0.6rem;
  font-size: 0.8rem;
  color: #444;
}

.field input,
.field select {
  padding: 0.4rem 0.5rem;
  border: 1px solid #ccc;
  border-radius: 0.35rem;
  font: inherit;
  font-size: 0.9rem;
}

.ai-attach {
  margin-bottom: 0.6rem;
}

.file-label {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  font-size: 0.8rem;
  color: #444;
}

.file-label input {
  font: inherit;
  font-size: 0.8rem;
}

.file-chip {
  display: inline-flex;
  align-items: center;
  gap: 0.4rem;
  margin: 0.4rem 0 0;
  padding: 0.2rem 0.5rem;
  font-size: 0.8rem;
  color: #2b56b5;
  background: #eef4ff;
  border: 1px solid #b9cdf5;
  border-radius: 4px;
}

.file-clear {
  border: none;
  background: none;
  font-size: 1rem;
  line-height: 1;
  color: #2b56b5;
  cursor: pointer;
}

.primary {
  padding: 0.45rem 0.9rem;
  border: 1px solid #1d4ed8;
  border-radius: 4px;
  background: #1d4ed8;
  color: #fff;
  font: inherit;
  font-size: 0.9rem;
  cursor: pointer;
}

.primary:hover {
  background: #1a44be;
}

.primary:disabled {
  opacity: 0.55;
  cursor: not-allowed;
}

/* --- Build page ---------------------------------------------------------- */

.build-form {
  max-width: 24rem;
}

.build-form small {
  margin-top: 0.15rem;
}

/* Sits between the form picker and the editor workspace; separate it from both. */
.refine {
  margin: 0.6rem 0 1rem;
}

/* The snippet library controls, above the editor workspace. */
.snippets {
  margin-bottom: 1rem;
}

.snippet-controls {
  display: flex;
  flex-wrap: wrap;
  gap: 1rem 2rem;
  align-items: flex-end;
}

.snippet-controls .field {
  margin-bottom: 0;
}

.snippet-insert {
  min-width: 16rem;
}

/* Name + kind + button on one row, wrapping on narrow widths. */
.snippet-save {
  display: flex;
  flex-wrap: wrap;
  align-items: flex-end;
  gap: 0.5rem;
}

.snippet-name {
  min-width: 14rem;
}

.snippet-kind {
  min-width: 7rem;
}

.workspace {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 1rem;
  align-items: start;
}

@media (max-width: 60rem) {
  .workspace {
    grid-template-columns: 1fr;
  }
}

.editor-pane,
.preview-pane {
  border: 1px solid #e2e2e2;
  border-radius: 0.5rem;
  overflow: hidden;
  background: #fff;
}

.toolbar {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 0.25rem;
  padding: 0.4rem 0.5rem;
  border-bottom: 1px solid #e2e2e2;
  background: #fafafa;
}

.tb {
  min-width: 2rem;
  padding: 0.3rem 0.5rem;
  border: 1px solid transparent;
  border-radius: 0.3rem;
  background: transparent;
  cursor: pointer;
  font-size: 0.9rem;
  line-height: 1;
}

.tb:hover {
  background: #ececec;
}

.tb.on {
  background: #e0edff;
  border-color: #b6d0ff;
  color: #1d4ed8;
}

.tb-sep {
  width: 1px;
  align-self: stretch;
  background: #ddd;
  margin: 0.1rem 0.25rem;
}

.tb-spacer {
  flex: 1;
}

.insert-field {
  padding: 0.35rem 0.5rem;
  border: 1px solid #ccc;
  border-radius: 0.3rem;
  font-size: 0.9rem;
}

.editor {
  min-height: 24rem;
  padding: 1rem 1.25rem;
}

/* The editor and preview surfaces are populated dynamically (ProseMirror /
   appended DOM), so their inner styling can't be scoped and uses :deep. */
.editor :deep(.ProseMirror) {
  min-height: 22rem;
  outline: none;
  line-height: 1.6;
}

.editor :deep(.ProseMirror p) {
  margin: 0 0 0.75rem;
}

.editor :deep(h1),
.preview :deep(h1) {
  font-size: 1.6rem;
}

.editor :deep(h2),
.preview :deep(h2) {
  font-size: 1.3rem;
}

.editor :deep(h3),
.preview :deep(h3) {
  font-size: 1.1rem;
}

.preview-head {
  display: flex;
  align-items: baseline;
  gap: 0.5rem;
  padding: 0.4rem 0.75rem;
  border-bottom: 1px solid #e2e2e2;
  background: #fafafa;
}

.preview-head h3 {
  margin: 0;
  font-size: 0.95rem;
}

.preview-head small {
  color: #888;
}

/* Picks which data the preview renders against: sample, or a captured response. */
.preview-source {
  display: inline-flex;
  align-items: center;
  gap: 0.35rem;
  margin-left: auto;
  font-size: 0.78rem;
  color: #888;
}

.preview-source select {
  padding: 0.2rem 0.35rem;
  border: 1px solid #ccc;
  border-radius: 0.3rem;
  font: inherit;
  font-size: 0.78rem;
  color: #444;
}

.preview-source select:disabled {
  opacity: 0.55;
  cursor: not-allowed;
}

.preview {
  padding: 1.25rem 1.5rem;
  min-height: 22rem;
  line-height: 1.6;
}

.preview :deep(.report-doc p) {
  margin: 0 0 0.75rem;
}

/* A filled merge value in the preview/PDF: readable inline, not a chip. */
.preview :deep(.merge-value) {
  background: #f2f6ff;
  border-radius: 0.2rem;
  padding: 0 0.15rem;
}

.preview :deep(.merge-missing) {
  color: #b91c1c;
  background: #fdecec;
  border-radius: 0.2rem;
  padding: 0 0.15rem;
  font-size: 0.9em;
}

/* --- Review page --------------------------------------------------------- */

.control-actions {
  display: flex;
  align-items: center;
  gap: 0.5rem;
}

.save-status {
  font-size: 0.85rem;
}

.hint {
  color: #888;
  font-size: 0.8rem;
}

.error {
  color: #b91c1c;
  font-size: 0.8rem;
}

.ok {
  color: #15803d;
  font-size: 0.8rem;
}
</style>
