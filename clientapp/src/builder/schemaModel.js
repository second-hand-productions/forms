import { FIELD_KIND, STEP_KIND, propsForType } from './fieldTypes.js'

// The builder needs stable per-field identity that survives reordering,
// renaming, and retyping. FormKit's `name` can't serve that purpose — the user
// edits it, and step 1 showed FormKitSchema patches positionally, so identity
// tied to array index or to `name` produces fields bound to the wrong data.
//
// So editor nodes carry a `_uid` that never changes for the life of the field,
// and a `_kind` distinguishing real fields from step markers. Both are stripped
// before rendering or saving: the server's allowlist rejects unknown props.
let uidCounter = 0

function nextUid() {
  uidCounter += 1
  return `f${uidCounter}`
}

/** Build a new editor node with sensible defaults for its type. */
export function createNode(type) {
  const index = uidCounter + 1
  const node = {
    _uid: nextUid(),
    _kind: FIELD_KIND,
    $formkit: type,
    name: `field${index}`,
    label: `Field ${index}`,
  }

  if (type === 'select' || type === 'radio') {
    node.options = { one: 'Option one', two: 'Option two' }
  }

  if (type === 'textarea') {
    node.rows = 4
  }

  return node
}

/**
 * A marker that starts a new step. Carries only a name and tab label.
 *
 * `stepNumber` is the step's position among steps, not its position in the
 * flat node list — numbering off the uid counter produces "Step 3" for the
 * second step, since fields share that counter.
 */
export function createStep(stepNumber) {
  return {
    _uid: nextUid(),
    _kind: STEP_KIND,
    name: `step${stepNumber}`,
    label: `Step ${stepNumber}`,
  }
}

export function isStep(node) {
  return node._kind === STEP_KIND
}

/** Attach editor identity to a flat schema loaded from the API. */
export function toEditorNodes(schema) {
  return schema.map((node) => ({ ...node, _uid: nextUid(), _kind: FIELD_KIND }))
}

/**
 * Inverse of toSchema: turn a stored or generated FormKit schema back into the
 * editor's flat list. A multi-step schema is flattened into fields plus step
 * markers, so anything the API returns is editable with the same drag, retype,
 * and rename affordances as a form built by hand.
 */
export function fromSchema(schema) {
  if (!Array.isArray(schema)) return []

  const multiStep = schema.find((node) => node?.$formkit === 'multi-step')
  if (!multiStep) return toEditorNodes(schema)

  const nodes = []

  ;(multiStep.children ?? []).forEach((step, index) => {
    // Emit a marker for every step including the first. groupIntoSteps starts a
    // new group at a marker and fills it with the fields that follow, so a
    // leading marker does not produce an empty step — and dropping it would
    // silently discard the first step's label on the way back out.
    nodes.push({
      _uid: nextUid(),
      _kind: STEP_KIND,
      name: step.name ?? `step${index + 1}`,
      label: step.label ?? `Step ${index + 1}`,
    })

    for (const field of step.children ?? []) {
      nodes.push({ ...field, _uid: nextUid(), _kind: FIELD_KIND })
    }
  })

  return nodes
}

function stripEditorProps(node) {
  const { _uid, _kind, ...rest } = node
  return rest
}

/**
 * Group the flat editor list into steps. Fields appearing before the first
 * step marker are collected into an implicit leading step so no field is
 * silently dropped.
 */
export function groupIntoSteps(nodes) {
  const groups = []
  let current = null

  for (const node of nodes) {
    if (isStep(node)) {
      current = { step: node, fields: [] }
      groups.push(current)
      continue
    }

    if (!current) {
      current = { step: null, fields: [] }
      groups.push(current)
    }

    current.fields.push(node)
  }

  return groups
}

/**
 * Strip editor-only fields and, when step markers are present, nest the result
 * into the multi-step structure @formkit/addons expects:
 *   multi-step > step[] > fields[]
 * With no step markers the output stays flat, exactly as before.
 */
export function toSchema(nodes, { multiStepName = 'steps' } = {}) {
  const hasSteps = nodes.some(isStep)

  if (!hasSteps) {
    return nodes.map(stripEditorProps)
  }

  const children = groupIntoSteps(nodes)
    // A step with no fields renders an empty tab; drop it rather than emit it.
    .filter((group) => group.fields.length > 0)
    .map((group, index) => ({
      $formkit: 'step',
      name: group.step?.name ?? `step${index + 1}`,
      label: group.step?.label ?? `Step ${index + 1}`,
      children: group.fields.map(stripEditorProps),
    }))

  if (children.length === 0) return []

  return [
    {
      $formkit: 'multi-step',
      name: multiStepName,
      children,
    },
  ]
}

/**
 * Change a field's type, dropping props that don't apply to the new type.
 * Without this, retyping a textarea to a checkbox would carry `rows` along and
 * the server would reject the save with a confusing per-prop error.
 */
export function retypeNode(node, nextType) {
  const allowed = propsForType(nextType)
  const result = { _uid: node._uid, _kind: node._kind, $formkit: nextType }

  for (const [key, value] of Object.entries(node)) {
    if (key === '_uid' || key === '_kind' || key === '$formkit') continue
    if (allowed.has(key)) result[key] = value
  }

  if ((nextType === 'select' || nextType === 'radio') && !result.options) {
    result.options = { one: 'Option one', two: 'Option two' }
  }

  if (nextType === 'textarea' && result.rows === undefined) {
    result.rows = 4
  }

  return result
}

/**
 * Field names become object keys on submit, so they must be unique — but only
 * within their step, since each step is its own group in the submitted data.
 * Step names must likewise be unique among themselves.
 */
export function findDuplicateNames(nodes) {
  const dupes = new Set()

  const check = (items) => {
    const seen = new Set()
    for (const item of items) {
      const name = (item.name ?? '').trim()
      if (!name) continue
      if (seen.has(name)) dupes.add(name)
      seen.add(name)
    }
  }

  const groups = groupIntoSteps(nodes)
  for (const group of groups) {
    check(group.fields)
  }

  check(groups.map((g) => g.step).filter(Boolean))

  return dupes
}
