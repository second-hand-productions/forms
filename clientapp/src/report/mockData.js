import { fromSchema, isStep } from '../builder/schemaModel.js'
import { mergeKey } from './renderTemplate.js'

// Turns a saved form's schema into the merge-field source list and a matching
// map of sample values. Real submission capture doesn't exist yet, so the
// report editor previews against these mocks — but the keys are built exactly
// as a real nested submission would be (step-aware, via mergeKey), so wiring in
// real data later is a drop-in, not a rework.

/**
 * Flatten a form schema into merge-field candidates: `{ name, label, step, type,
 * options }`. Fields before the first step marker (or in a single-step form)
 * carry `step: null`; fields inside a step carry that step's name, so duplicate
 * names across steps stay distinct.
 */
export function deriveFields(schema) {
  const nodes = fromSchema(schema)
  const fields = []
  let currentStep = null

  for (const node of nodes) {
    if (isStep(node)) {
      currentStep = node.name ?? null
      continue
    }

    if (!node.name) continue

    fields.push({
      name: node.name,
      label: node.label ?? node.name,
      step: currentStep,
      type: node.$formkit,
      options: node.options ?? null,
    })
  }

  return fields
}

function firstOptionLabel(options) {
  if (!options || typeof options !== 'object') return null
  const values = Object.values(options)
  return values.length ? String(values[0]) : null
}

/** A plausible sample value for a field, so previews look like real documents. */
function sampleValue(field) {
  switch (field.type) {
    case 'email':
      return 'ada@example.com'
    case 'number':
      return '42'
    case 'textarea':
      return 'A longer sample response that spans a sentence or two, so the layout is easy to judge.'
    case 'select':
    case 'radio':
      return firstOptionLabel(field.options) ?? 'Option one'
    case 'checkbox':
      return 'Yes'
    case 'date':
      return '2026-07-30'
    case 'tel':
      return '(555) 123-4567'
    case 'url':
      return 'https://example.com'
    default:
      return `Sample ${field.label}`
  }
}

/**
 * Build the `{ key: value }` map the renderer looks up, keyed identically to how
 * merge fields resolve (see mergeKey).
 */
export function buildMockData(fields) {
  const data = {}
  for (const field of fields) {
    data[mergeKey(field)] = sampleValue(field)
  }
  return data
}
