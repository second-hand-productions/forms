import { Node, mergeAttributes } from '@tiptap/core'
import { VueNodeViewRenderer } from '@tiptap/vue-3'
import MergeChip from './MergeChip.vue'

/**
 * A merge field: an inline, atomic placeholder that binds to one form field.
 *
 * `atom: true` is the point — the chip is one opaque unit the user can insert or
 * delete but never type inside or partially edit, so it behaves like a token
 * rather than editable text. It stores the field's `name` (the durable binding
 * key into submission data), a human `label` for display, and the owning `step`
 * name for multi-step forms (null for single-step), so two fields that share a
 * name across steps resolve to different values.
 *
 * `renderHTML`/`parseHTML` handle copy-paste and any non-editor serialization;
 * the Vue node view is the in-editor appearance.
 */
export const MergeField = Node.create({
  name: 'mergeField',
  group: 'inline',
  inline: true,
  atom: true,
  selectable: true,
  draggable: false,

  addAttributes() {
    return {
      name: {
        default: null,
        parseHTML: (el) => el.getAttribute('data-name'),
        renderHTML: (attrs) => (attrs.name ? { 'data-name': attrs.name } : {}),
      },
      label: {
        default: null,
        parseHTML: (el) => el.getAttribute('data-label'),
        renderHTML: (attrs) => (attrs.label ? { 'data-label': attrs.label } : {}),
      },
      step: {
        default: null,
        parseHTML: (el) => el.getAttribute('data-step'),
        renderHTML: (attrs) => (attrs.step ? { 'data-step': attrs.step } : {}),
      },
    }
  },

  parseHTML() {
    return [{ tag: 'span[data-merge-field]' }]
  },

  renderHTML({ HTMLAttributes, node }) {
    return [
      'span',
      mergeAttributes(HTMLAttributes, { 'data-merge-field': '', class: 'merge-chip' }),
      node.attrs.label || node.attrs.name || 'field',
    ]
  },

  addNodeView() {
    return VueNodeViewRenderer(MergeChip)
  },

  addCommands() {
    return {
      insertMergeField:
        (attrs) =>
        ({ commands }) =>
          commands.insertContent({ type: this.name, attrs }),
    }
  },
})
