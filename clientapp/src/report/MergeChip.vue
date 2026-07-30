<script setup>
import { computed } from 'vue'
import { NodeViewWrapper } from '@tiptap/vue-3'

// Props supplied by TipTap's Vue node-view renderer. `node` holds the merge
// field's attrs; `deleteNode` removes this atom from the document.
const props = defineProps({
  node: { type: Object, required: true },
  deleteNode: { type: Function, required: true },
})

const display = computed(() => props.node.attrs.label || props.node.attrs.name || 'field')
</script>

<template>
  <!-- as="span" keeps the chip inline within a paragraph. contenteditable=false
       stops the caret from entering the atom. -->
  <NodeViewWrapper as="span" class="merge-chip" contenteditable="false" :title="node.attrs.name">
    <span class="merge-chip-label">{{ display }}</span>
    <button
      type="button"
      class="merge-chip-remove"
      aria-label="Remove merge field"
      @click="deleteNode()"
    >
      ×
    </button>
  </NodeViewWrapper>
</template>

<style scoped>
.merge-chip {
  display: inline-flex;
  align-items: center;
  gap: 0.25rem;
  padding: 0.05rem 0.2rem 0.05rem 0.45rem;
  margin: 0 1px;
  border-radius: 0.75rem;
  background: #e0edff;
  color: #1d4ed8;
  border: 1px solid #b6d0ff;
  font-size: 0.85em;
  line-height: 1.4;
  white-space: nowrap;
  user-select: none;
}

.merge-chip-label {
  font-weight: 500;
}

.merge-chip-remove {
  border: none;
  background: transparent;
  color: #1d4ed8;
  cursor: pointer;
  font-size: 1em;
  line-height: 1;
  padding: 0 0.15rem;
  border-radius: 50%;
}

.merge-chip-remove:hover {
  background: #b6d0ff;
}
</style>
