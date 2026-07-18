// A FormKit schema: plain JSON, no Vue markup.
//
// This is the single source of truth for the POC. Everything downstream is a
// producer or consumer of this shape:
//   - the drag-and-drop builder edits this array
//   - the AI prompt generates this array
//   - the API stores this array
//   - <FormKitSchema> renders it
//
// Keep it JSON-serializable — no functions, no imports, no Vue components.
// Anything that can't survive a round-trip through the API doesn't belong here.
export const contactFormSchema = [
  {
    $formkit: 'text',
    name: 'fullName',
    label: 'Full name',
    placeholder: 'Ada Lovelace',
    validation: 'required',
  },
  {
    $formkit: 'email',
    name: 'email',
    label: 'Email',
    placeholder: 'ada@example.com',
    validation: 'required|email',
  },
  {
    $formkit: 'number',
    name: 'teamSize',
    label: 'Team size',
    help: 'How many people will use this form?',
    validation: 'required|min:1|max:500',
  },
  {
    $formkit: 'select',
    name: 'plan',
    label: 'Plan',
    options: {
      free: 'Free',
      pro: 'Pro',
      enterprise: 'Enterprise',
    },
    validation: 'required',
  },
  {
    $formkit: 'radio',
    name: 'contactMethod',
    label: 'Preferred contact method',
    options: {
      email: 'Email',
      phone: 'Phone',
      none: "Don't contact me",
    },
    validation: 'required',
  },
  {
    $formkit: 'textarea',
    name: 'notes',
    label: 'Notes',
    rows: 4,
    validation: 'length:0,500',
  },
  {
    $formkit: 'checkbox',
    name: 'subscribe',
    label: 'Send me product updates',
  },
]
