<script setup>
import { ref } from 'vue'
import { NodeViewWrapper } from '@tiptap/vue-3'
import { apiUrl } from '../api.js'

// Props supplied by TipTap's Vue node-view renderer. `node` holds the reference's
// attrs; `editor`/`getPos` locate this atom for Detach; `deleteNode` removes it.
const props = defineProps({
  node: { type: Object, required: true },
  editor: { type: Object, required: true },
  getPos: { type: Function, required: true },
  deleteNode: { type: Function, required: true },
})

const detachState = ref({ status: 'idle', message: '' })

const display = () => props.node.attrs.name || props.node.attrs.id || 'block'

// Replace the reference with a copy of the block's current content — the escape
// hatch for diverging from the shared snippet in this one report. After this the
// nodes are ordinary editable content with no further link to the block.
async function detach() {
  const id = props.node.attrs.id
  if (!id) return

  detachState.value = { status: 'working', message: '' }
  try {
    const res = await fetch(apiUrl(`/blocks/${id}`))
    if (!res.ok) throw new Error(`HTTP ${res.status}`)
    const block = await res.json()
    const childrenJson = block.content?.content ?? []

    // Locate this reference node fresh in the current document rather than
    // trusting a captured position: its pos and nodeSize then come from the same
    // node instance in the same state, so the replace range is always valid.
    const { state, view } = props.editor
    let target = null
    state.doc.descendants((node, pos) => {
      if (target) return false
      if (node.type.name === 'blockRef' && node.attrs.id === id) target = { node, pos }
      return !target
    })
    if (!target) throw new Error('Could not locate the snippet.')

    // Rebuild the block's nodes against the editor's schema and swap them in for
    // the reference. An empty block just removes the reference, a reasonable
    // outcome. Focus returns to the editor so the caret lands in the new content.
    const nodes = childrenJson.map((json) => state.schema.nodeFromJSON(json))
    view.dispatch(state.tr.replaceWith(target.pos, target.pos + target.node.nodeSize, nodes))
    view.focus()
  } catch (err) {
    detachState.value = { status: 'error', message: err.message }
  }
}
</script>

<template>
  <!-- Block-level placeholder. contenteditable=false keeps the caret out of the
       atom; the referenced content is shown resolved in the preview, not here. -->
  <NodeViewWrapper class="block-ref" contenteditable="false" :title="node.attrs.id">
    <span class="block-ref-icon" aria-hidden="true">🧩</span>
    <span class="block-ref-body">
      <span class="block-ref-label">{{ display() }}</span>
      <span class="block-ref-note">Live snippet — updates when the snippet changes</span>
      <span v-if="detachState.status === 'error'" class="block-ref-error">{{ detachState.message }}</span>
    </span>
    <span class="block-ref-tools">
      <button
        type="button"
        class="block-ref-btn"
        :disabled="detachState.status === 'working'"
        title="Replace this reference with an editable copy"
        @click="detach"
      >
        {{ detachState.status === 'working' ? 'Detaching…' : 'Detach' }}
      </button>
      <button
        type="button"
        class="block-ref-btn block-ref-remove"
        aria-label="Remove snippet reference"
        title="Remove this reference"
        @click="deleteNode()"
      >
        ×
      </button>
    </span>
  </NodeViewWrapper>
</template>

<style scoped>
.block-ref {
  display: flex;
  align-items: center;
  gap: 0.6rem;
  margin: 0 0 0.75rem;
  padding: 0.5rem 0.65rem;
  border: 1px dashed #b6d0ff;
  border-left: 3px solid #4a7adf;
  border-radius: 0.4rem;
  background: #f7faff;
  user-select: none;
}

.block-ref-icon {
  font-size: 1.1rem;
  line-height: 1;
}

.block-ref-body {
  display: flex;
  flex-direction: column;
  gap: 0.1rem;
  min-width: 0;
  flex: 1;
}

.block-ref-label {
  font-weight: 600;
  color: #2b56b5;
  font-size: 0.9rem;
}

.block-ref-note {
  font-size: 0.72rem;
  color: #7789a8;
}

.block-ref-error {
  font-size: 0.72rem;
  color: #b91c1c;
}

.block-ref-tools {
  display: flex;
  align-items: center;
  gap: 0.3rem;
}

.block-ref-btn {
  padding: 0.2rem 0.5rem;
  font: inherit;
  font-size: 0.78rem;
  color: #2b56b5;
  background: #fff;
  border: 1px solid #b6d0ff;
  border-radius: 0.3rem;
  cursor: pointer;
}

.block-ref-btn:hover {
  background: #e0edff;
}

.block-ref-btn:disabled {
  opacity: 0.6;
  cursor: not-allowed;
}

.block-ref-remove {
  color: #b91c1c;
  border-color: #e3b7bf;
  font-size: 1rem;
  line-height: 1;
  padding: 0.1rem 0.4rem;
}

.block-ref-remove:hover {
  background: #fdf2f4;
}
</style>
