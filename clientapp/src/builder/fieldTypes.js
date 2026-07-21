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
  { type: 'radio', label: 'Radio group', props: ['help', 'validation', 'options', 'optionsLayout'] },
  { type: 'checkbox', label: 'Checkbox', props: ['help', 'validation'] },
  { type: 'date', label: 'Date', props: ['help', 'validation'] },
  { type: 'tel', label: 'Phone', props: ['placeholder', 'help', 'validation'] },
  { type: 'url', label: 'URL', props: ['placeholder', 'help', 'validation'] },
]

// Props every field carries regardless of type. columnSpan is here rather than
// per-type so retyping never drops a field's width — layout survives a text
// field becoming a dropdown.
export const COMMON_PROPS = ['name', 'label', 'columnSpan']

// Fields lay out on a 12-column grid. A field's columnSpan is how many of those
// columns it occupies; fields flow left to right and wrap into a new row when
// the next one no longer fits, so rows are implied by the spans rather than
// declared. 12 divides evenly by 2, 3 and 4, which is what makes halves, thirds
// and quarters expressible on one track count.
export const GRID_COLUMNS = 12

/** Widths offered in the property editor, coarse enough to stay predictable. */
export const COLUMN_SPANS = [
  { span: 12, label: 'Full width' },
  { span: 6, label: 'Half' },
  { span: 4, label: 'Third' },
  { span: 3, label: 'Quarter' },
  { span: 8, label: 'Two thirds' },
  { span: 9, label: 'Three quarters' },
]

/** A field with no columnSpan predates the feature and renders full width. */
export const DEFAULT_COLUMN_SPAN = GRID_COLUMNS

export function normalizeColumnSpan(value) {
  const span = Math.trunc(Number(value))
  if (!Number.isFinite(span) || span < 1) return DEFAULT_COLUMN_SPAN
  return Math.min(span, GRID_COLUMNS)
}

// How a radio group arranges its options. Named layouts rather than a class
// name, for the same reason columnSpan is an integer: the server can validate a
// closed set, and the client resolves it to a class at render time.
export const OPTIONS_LAYOUTS = [
  { value: 'vertical', label: 'Stacked' },
  { value: 'horizontal', label: 'Side by side' },
]

/** A field with no layout predates the feature and stacks, as genesis does. */
export const DEFAULT_OPTIONS_LAYOUT = 'vertical'

export function normalizeOptionsLayout(value) {
  return OPTIONS_LAYOUTS.some((layout) => layout.value === value)
    ? value
    : DEFAULT_OPTIONS_LAYOUT
}

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
