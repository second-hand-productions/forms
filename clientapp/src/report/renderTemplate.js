import { apiUrl } from '../api.js'

// Render a TipTap document to DOM, filling merge fields from a submission's data.
//
// This walks the document JSON and builds real DOM nodes rather than an HTML
// string. That is the security boundary: every text value and every merge value
// becomes a `document.createTextNode`, which the browser escapes by
// construction. A submission value of `<script>…` therefore renders as literal
// text — there is no `v-html` and no string interpolation to exploit.

const HEADING_LEVELS = new Set([1, 2, 3])

const MARK_TAGS = {
  bold: 'strong',
  italic: 'em',
  strike: 's',
}

// Block-level styling is applied inline rather than via a stylesheet so the
// rendered element is self-contained: the PDF exporter rasterizes it detached
// from the app, where the preview pane's (scoped) CSS would not reach it.
// Without this, paragraphs and headings collapse together in the exported PDF.
const ROOT_STYLE =
  'font-family: Arial, Helvetica, sans-serif; font-size: 14px; line-height: 1.6; color: #1a1a1a;'

const BLOCK_STYLES = {
  p: 'margin: 0 0 0.75em;',
  h1: 'margin: 0 0 0.5em; font-size: 1.7em; font-weight: 700; line-height: 1.25;',
  h2: 'margin: 0 0 0.5em; font-size: 1.35em; font-weight: 700; line-height: 1.3;',
  h3: 'margin: 0 0 0.4em; font-size: 1.15em; font-weight: 700; line-height: 1.3;',
  ul: 'margin: 0 0 0.75em; padding-left: 1.5em;',
  ol: 'margin: 0 0 0.75em; padding-left: 1.5em;',
  li: 'margin: 0 0 0.25em;',
}

/**
 * The key a merge field looks up in the data map. Multi-step submissions nest
 * values under the step name, so a field carrying a `step` resolves to
 * `step.name`; single-step fields resolve to the bare `name`. Kept in sync with
 * buildMockData so mock and (later) real data use identical keys.
 */
export function mergeKey(attrs) {
  const name = attrs?.name ?? ''
  return attrs?.step ? `${attrs.step}.${name}` : name
}

function renderMergeField(node, dataMap) {
  const span = document.createElement('span')
  span.className = 'merge-value'

  const key = mergeKey(node.attrs ?? {})
  const has = dataMap && Object.prototype.hasOwnProperty.call(dataMap, key)

  if (has) {
    span.textContent = formatValue(dataMap[key])
  } else {
    // The source form may have been edited or the field removed since the
    // template was authored. Show a visible marker instead of silently blank.
    span.className = 'merge-missing'
    span.textContent = `[missing: ${node.attrs?.label || node.attrs?.name || 'field'}]`
  }

  return span
}

function formatValue(value) {
  if (value === null || value === undefined) return ''
  if (Array.isArray(value)) return value.join(', ')
  return String(value)
}

// A live snippet reference. The referenced block's document is looked up in
// blockMap (id -> block `doc`, resolved by the caller) and its content rendered
// inline against the same data. A block cannot itself contain a blockRef (the
// server refuses it), so this never recurses into another reference — no cycle
// guard is needed. An id that doesn't resolve (block deleted, or not yet loaded)
// shows a visible marker, mirroring how a missing merge field is handled.
function renderBlockRef(node, dataMap, blockMap) {
  const id = node.attrs?.id
  const block = id ? blockMap?.[id] : null

  if (!block) {
    const span = document.createElement('span')
    span.className = 'merge-missing'
    span.textContent = `[missing snippet: ${node.attrs?.name || id || 'block'}]`
    return span
  }

  // The block is a `doc`; splice its children in via a fragment so the reference
  // adds no wrapper element of its own to the output.
  const fragment = document.createDocumentFragment()
  appendChildren(fragment, block, dataMap, blockMap)
  return fragment
}

// An uploaded image asset. The src is always built from the asset id served by
// the API — never taken from stored content — so a template can only ever point
// at an asset by id, not at an arbitrary URL. An id that no longer resolves just
// renders a broken-image element (the asset was deleted); missing id renders
// nothing.
function renderImage(node) {
  const assetId = node.attrs?.assetId
  if (!assetId) return null

  const img = document.createElement('img')
  img.src = apiUrl(`/assets/${assetId}/content`)
  if (node.attrs?.alt) img.alt = node.attrs.alt
  // Self-contained sizing so the exported PDF (rendered detached from the app's
  // stylesheet) keeps the image within the page width.
  img.setAttribute('style', 'max-width: 100%; height: auto;')
  return img
}

function renderText(node) {
  let el = document.createTextNode(node.text ?? '')

  // Wrap the text node once per mark. Order is cosmetic since these are all
  // inline formatting elements.
  for (const mark of node.marks ?? []) {
    const tag = MARK_TAGS[mark?.type]
    if (!tag) continue
    const wrapper = document.createElement(tag)
    wrapper.appendChild(el)
    el = wrapper
  }

  return el
}

function renderNode(node, dataMap, blockMap) {
  switch (node?.type) {
    case 'text':
      return renderText(node)

    case 'mergeField':
      return renderMergeField(node, dataMap)

    case 'blockRef':
      return renderBlockRef(node, dataMap, blockMap)

    case 'image':
      return renderImage(node)

    case 'hardBreak':
      return document.createElement('br')

    case 'paragraph':
      return renderContainer('p', node, dataMap, blockMap)

    case 'heading': {
      const level = HEADING_LEVELS.has(node.attrs?.level) ? node.attrs.level : 1
      return renderContainer(`h${level}`, node, dataMap, blockMap)
    }

    case 'bulletList':
      return renderContainer('ul', node, dataMap, blockMap)

    case 'orderedList':
      return renderContainer('ol', node, dataMap, blockMap)

    case 'listItem':
      return renderContainer('li', node, dataMap, blockMap)

    default:
      // Unknown types can't appear in validated content, but stay defensive:
      // render nothing rather than throw.
      return null
  }
}

function renderContainer(tag, node, dataMap, blockMap) {
  const el = document.createElement(tag)
  if (BLOCK_STYLES[tag]) el.setAttribute('style', BLOCK_STYLES[tag])
  appendChildren(el, node, dataMap, blockMap)
  return el
}

function appendChildren(el, node, dataMap, blockMap) {
  for (const child of node?.content ?? []) {
    const rendered = renderNode(child, dataMap, blockMap)
    if (rendered) el.appendChild(rendered)
  }
}

/**
 * Collect the ids of every blockRef (live snippet reference) in a doc, so the
 * caller can fetch those blocks and build the blockMap renderTemplate consumes.
 * Returns a Set; a doc with no references yields an empty one.
 */
export function collectBlockRefIds(doc) {
  const ids = new Set()

  const walk = (node) => {
    if (!node || typeof node !== 'object') return
    if (node.type === 'blockRef' && node.attrs?.id) ids.add(node.attrs.id)
    for (const child of node.content ?? []) walk(child)
  }

  walk(doc)
  return ids
}

/**
 * Render a validated TipTap doc against a data map into a styled container
 * element. The element is detached — the caller mounts it (preview) and/or
 * hands it to the PDF exporter.
 *
 * blockMap resolves live snippet references (blockRef nodes) to the referenced
 * block's `doc`; see collectBlockRefIds for building it. Omit it for a doc with
 * no references — unresolved references render a visible [missing snippet] marker.
 */
export function renderTemplate(doc, dataMap = {}, blockMap = {}) {
  const root = document.createElement('div')
  root.className = 'report-doc'
  root.setAttribute('style', ROOT_STYLE)

  if (doc?.type === 'doc') {
    appendChildren(root, doc, dataMap, blockMap)
  }

  return root
}
