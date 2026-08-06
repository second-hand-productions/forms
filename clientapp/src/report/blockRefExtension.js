import { Node, mergeAttributes } from '@tiptap/core'
import { VueNodeViewRenderer } from '@tiptap/vue-3'
import BlockRefView from './BlockRefView.vue'

/**
 * A live snippet reference: a block-level, atomic placeholder that transcludes a
 * reusable Block by id. Unlike a copy-in insert (Phase 1), editing the referenced
 * block later changes every report that references it.
 *
 * `atom: true` makes it one opaque unit — the editor shows a labelled placeholder
 * chip (the Vue node view), not the block's content; the content is resolved and
 * rendered in the *preview* (see renderTemplate's blockMap), the same split the
 * merge-field chip uses. It stores the block's `id` (the durable binding key) and
 * a `name` cached for display and as a fallback label if the block is gone.
 *
 * The block-level group and `draggable` let it be moved between paragraphs like
 * any block. "Detach" (in the node view) replaces the reference with a copy of the
 * block's current content, the escape hatch for diverging in one report.
 *
 * `renderHTML`/`parseHTML` handle copy-paste and non-editor serialization; the Vue
 * node view is the in-editor appearance.
 */
export const BlockRef = Node.create({
  name: 'blockRef',
  group: 'block',
  atom: true,
  selectable: true,
  draggable: true,

  addAttributes() {
    return {
      id: {
        default: null,
        parseHTML: (el) => el.getAttribute('data-block-id'),
        renderHTML: (attrs) => (attrs.id ? { 'data-block-id': attrs.id } : {}),
      },
      name: {
        default: null,
        parseHTML: (el) => el.getAttribute('data-block-name'),
        renderHTML: (attrs) => (attrs.name ? { 'data-block-name': attrs.name } : {}),
      },
    }
  },

  parseHTML() {
    return [{ tag: 'div[data-block-ref]' }]
  },

  renderHTML({ HTMLAttributes, node }) {
    return [
      'div',
      mergeAttributes(HTMLAttributes, { 'data-block-ref': '', class: 'block-ref-chip' }),
      `Snippet: ${node.attrs.name || node.attrs.id || 'block'}`,
    ]
  },

  addNodeView() {
    return VueNodeViewRenderer(BlockRefView)
  },

  addCommands() {
    return {
      insertBlockRef:
        (attrs) =>
        ({ commands }) =>
          commands.insertContent({ type: this.name, attrs }),
    }
  },
})
