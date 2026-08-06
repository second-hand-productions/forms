import { Node, mergeAttributes } from '@tiptap/core'
import { apiUrl } from '../api.js'

/**
 * An uploaded image asset (logo, letterhead, branding), referenced by asset id.
 *
 * Inline and atomic, so a logo can sit in a line of text and is inserted or
 * deleted as one unit. The only stored attrs are `assetId` (the durable binding
 * key) and an optional `alt`; the displayed `src` is *derived* from the id in
 * renderHTML, never stored — the same reference-by-id discipline as merge fields
 * and block references, and what lets the server forbid a raw `src`.
 *
 * parseHTML matches only `img[data-asset-id]`, so a pasted off-site `<img>` is
 * not adopted as one of these nodes (StarterKit has no image node, so it is
 * simply dropped) — arbitrary external image URLs never enter the document.
 */
export const Image = Node.create({
  name: 'image',
  group: 'inline',
  inline: true,
  atom: true,
  selectable: true,
  draggable: true,

  addAttributes() {
    return {
      assetId: {
        default: null,
        parseHTML: (el) => el.getAttribute('data-asset-id'),
        renderHTML: (attrs) => (attrs.assetId ? { 'data-asset-id': attrs.assetId } : {}),
      },
      alt: {
        default: null,
        parseHTML: (el) => el.getAttribute('alt'),
        renderHTML: (attrs) => (attrs.alt ? { alt: attrs.alt } : {}),
      },
    }
  },

  parseHTML() {
    return [{ tag: 'img[data-asset-id]' }]
  },

  renderHTML({ HTMLAttributes, node }) {
    const src = node.attrs.assetId ? apiUrl(`/assets/${node.attrs.assetId}/content`) : ''
    // src is added here for display only; it is not an attribute of the node, so
    // getJSON persists just assetId + alt.
    return ['img', mergeAttributes(HTMLAttributes, { src, class: 'report-image' })]
  },

  addCommands() {
    return {
      insertImage:
        (attrs) =>
        ({ commands }) =>
          commands.insertContent({ type: this.name, attrs }),
    }
  },
})
