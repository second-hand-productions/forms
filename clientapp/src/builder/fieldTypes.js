// Palette of field types the builder can produce.
//
// This list is deliberately a subset of the server's allowlist in
// forms/Validation/FormSchemaValidator.cs. The server is the security
// boundary; this is the UI affordance. If you add a type here it must also
// exist there, or saving will 400.
export const FIELD_TYPES = [
  { type: 'text', label: 'Text', props: ['placeholder', 'help', 'validation'] },
  { type: 'email', label: 'Email', props: ['placeholder', 'help', 'validation'] },
  { type: 'number', label: 'Number', props: ['help', 'validation', 'min', 'max', 'step'] },
  { type: 'textarea', label: 'Text area', props: ['placeholder', 'help', 'validation', 'rows'] },
  { type: 'select', label: 'Dropdown', props: ['help', 'validation', 'options'] },
  { type: 'radio', label: 'Radio group', props: ['help', 'validation', 'options'] },
  { type: 'checkbox', label: 'Checkbox', props: ['help', 'validation'] },
  { type: 'date', label: 'Date', props: ['help', 'validation'] },
  { type: 'tel', label: 'Phone', props: ['placeholder', 'help', 'validation'] },
  { type: 'url', label: 'URL', props: ['placeholder', 'help', 'validation'] },
]

// Props every field carries regardless of type.
export const COMMON_PROPS = ['name', 'label']

// Step markers are an editor construct, not a FormKit type. They split the flat
// field list into steps, which toSchema() converts into the nested
// multi-step > step > fields structure FormKit's addon expects. Keeping the
// editor list flat means one sortable list, so drag/reorder logic is unchanged.
export const STEP_KIND = 'step'
export const FIELD_KIND = 'field'

export function getFieldType(type) {
  return FIELD_TYPES.find((f) => f.type === type)
}

/** Props valid for a type — used to drop stale props when retyping a field. */
export function propsForType(type) {
  const def = getFieldType(type)
  return new Set([...COMMON_PROPS, ...(def?.props ?? [])])
}
