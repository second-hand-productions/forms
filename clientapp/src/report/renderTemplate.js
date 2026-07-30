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

function renderNode(node, dataMap) {
  switch (node?.type) {
    case 'text':
      return renderText(node)

    case 'mergeField':
      return renderMergeField(node, dataMap)

    case 'hardBreak':
      return document.createElement('br')

    case 'paragraph':
      return renderContainer('p', node, dataMap)

    case 'heading': {
      const level = HEADING_LEVELS.has(node.attrs?.level) ? node.attrs.level : 1
      return renderContainer(`h${level}`, node, dataMap)
    }

    case 'bulletList':
      return renderContainer('ul', node, dataMap)

    case 'orderedList':
      return renderContainer('ol', node, dataMap)

    case 'listItem':
      return renderContainer('li', node, dataMap)

    default:
      // Unknown types can't appear in validated content, but stay defensive:
      // render nothing rather than throw.
      return null
  }
}

function renderContainer(tag, node, dataMap) {
  const el = document.createElement(tag)
  if (BLOCK_STYLES[tag]) el.setAttribute('style', BLOCK_STYLES[tag])
  appendChildren(el, node, dataMap)
  return el
}

function appendChildren(el, node, dataMap) {
  for (const child of node?.content ?? []) {
    const rendered = renderNode(child, dataMap)
    if (rendered) el.appendChild(rendered)
  }
}

/**
 * Render a validated TipTap doc against a data map into a styled container
 * element. The element is detached — the caller mounts it (preview) and/or
 * hands it to the PDF exporter.
 */
export function renderTemplate(doc, dataMap = {}) {
  const root = document.createElement('div')
  root.className = 'report-doc'
  root.setAttribute('style', ROOT_STYLE)

  if (doc?.type === 'doc') {
    appendChildren(root, doc, dataMap)
  }

  return root
}
