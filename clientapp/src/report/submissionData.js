import { deriveFields } from './mockData.js'
import { mergeKey } from './renderTemplate.js'

// Turn a FormKit submit payload into the flat `{ mergeKey: value }` map the report
// renderer consumes — the real counterpart to buildMockData, producing an
// identically shaped map so a captured submission is a drop-in for the sample
// data it replaces.

/**
 * Find a nested group object by name anywhere in the payload tree.
 *
 * A multi-step form nests each step's values under the step's name, so a step
 * field's value is not at the top level. The exact depth depends on FormKit's
 * multi-step wrapper (a field may sit at `payload[step]` or one level deeper), so
 * this searches rather than assuming a fixed path — the caller only knows the
 * step name, which is what mergeKey keys on.
 */
function findGroup(obj, name) {
  if (!obj || typeof obj !== 'object' || Array.isArray(obj)) return null

  const direct = obj[name]
  if (direct && typeof direct === 'object' && !Array.isArray(direct)) return direct

  for (const value of Object.values(obj)) {
    const found = findGroup(value, name)
    if (found) return found
  }
  return null
}

/**
 * Build the flat merge-key → value map for a submission, keyed exactly as
 * buildMockData and the renderer's mergeKey: step fields resolve to
 * `step.field`, single-step fields to the bare `field` name.
 *
 * @param {Array} schema  the form's stored FormKit schema (what GET /api/forms/:id returns)
 * @param {object} payload the object FormKit's @submit hands back
 */
export function buildSubmissionData(schema, payload) {
  const fields = deriveFields(schema)
  const data = {}

  for (const field of fields) {
    const value = field.step
      ? findGroup(payload, field.step)?.[field.name]
      : payload?.[field.name]

    // Include present-but-empty values (empty string, false) so the report shows
    // the real answer; only a genuinely absent field is left out, to render as a
    // [missing] marker rather than a misleading blank.
    if (value !== undefined) data[mergeKey(field)] = value
  }

  return data
}
