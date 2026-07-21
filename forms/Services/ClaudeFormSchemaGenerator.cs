using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using forms.Models;
using forms.Validation;

namespace forms.Services;

/// <summary>
/// Turns a natural-language prompt into a FormKit schema.
///
/// Two layers keep the output trustworthy:
///   1. Structured outputs constrain Claude to the <see cref="GeneratedForm"/>
///      shape, so we never parse free-form text or hand-written JSON.
///   2. The assembled schema still goes through <see cref="FormSchemaValidator"/>
///      before it is returned. Model output is untrusted input like any other —
///      the allowlist is the security boundary, not the prompt.
/// </summary>
public class ClaudeFormSchemaGenerator(
    AnthropicClient client,
    ILogger<ClaudeFormSchemaGenerator> logger) : IFormSchemaGenerator
{
    private const int MaxPromptLength = 2_000;

    /// <summary>
    /// Mirrors the client palette in clientapp/src/builder/fieldTypes.js and is a
    /// subset of the validator's allowlist. Constraining generation to this set
    /// means a well-formed generation is also a valid save.
    /// </summary>
    private static readonly string[] AllowedTypes =
    [
        "text", "email", "number", "textarea", "select",
        "radio", "checkbox", "date", "tel", "url",
    ];

    /// <summary>
    /// Widths the model may choose from, on a twelve-column grid.
    ///
    /// A subset of 1..12 rather than the full range, and deliberately the same
    /// set the builder's Width dropdown offers (COLUMN_SPANS in
    /// clientapp/src/builder/fieldTypes.js). A generated span of, say, 5 would
    /// validate and render, but the dropdown has no entry for it, so opening the
    /// form in the builder would show an empty Width control.
    /// </summary>
    private static readonly int[] AllowedColumnSpans = [3, 4, 6, 8, 9, 12];

    private const int FullWidthSpan = 12;

    /// <summary>
    /// Props the validator allows on a field but <see cref="GeneratedField"/> does
    /// not model, so the model never sees them and cannot return them.
    ///
    /// On a refinement they are copied across from the field of the same name in
    /// the form that was sent. Without that, every refine would silently reset a
    /// textarea's row count or a number's bounds to the defaults — the model
    /// would be deleting properties it was never shown, which is exactly the
    /// failure an incremental edit is supposed to avoid.
    /// </summary>
    private static readonly string[] CarriedProps =
    [
        "rows", "cols", "min", "max", "step",
        "value", "multiple", "disabled", "id", "validationLabel",
        "optionsLayout",
    ];

    /// <summary>
    /// What a well-formed field looks like. Shared verbatim by generation and
    /// refinement so a refined form obeys the same conventions as a generated
    /// one — otherwise every edit would drift the form away from house style.
    /// </summary>
    private const string FieldRules = """
        Rules:
        - `name` is the submitted data key: camelCase, letters and digits only, unique within a step.
        - `label` is what the user reads. Keep it short and human.
        - `validation` uses FormKit rules joined by "|" — e.g. "required", "required|email",
          "required|length:0,500". Use "" when the field is optional and unconstrained.
        - `options` applies only to select and radio. Leave it empty for every other type.
        - `placeholder` and `help` are optional; use "" when they'd add nothing.
        - No user-visible text may begin with "$" — FormKit would evaluate it as an
          expression. Write "50 USD" or "Under 100 dollars" rather than "$50", and put
          any currency symbol later in the string ("Budget in $" is fine).
        - `columnSpan` is the field's width on a twelve-column grid: 12 is full width, 6 is
          half, 4 is a third, 3 is a quarter. Fields flow left to right and wrap onto a new
          row when the next one no longer fits, so a row is formed by spans that add up to
          12 — two 6s, or a 4 and an 8, or four 3s.
        - Put naturally paired short fields side by side: first and last name, city and
          postcode, a date and a time. Keep anything the user types at length full width —
          textareas, addresses, email, URLs. When in doubt use 12; a form that is merely
          tidy beats one that is cramped.
        - Do not leave a row part-filled. If three fields would sit on a row, give them 4
          each rather than 6, 3 and 3, which wraps the last one on its own.
        - Only split into steps when a form is genuinely long (roughly 8+ fields) or has
          distinct phases. Set `startsNewStep` true on the first field of each step and give
          that field a `stepLabel`. For a single-step form set `startsNewStep` false everywhere
          and leave `stepLabel` empty.
        """;

    private const string SystemPrompt = """
        You design web forms. Given a description, produce the fields the form needs.

        """
        + FieldRules
        + """

        - Prefer the smallest set of fields that actually satisfies the request. Do not invent
          fields the user did not ask for or imply.
        """;

    /// <summary>
    /// The refine counterpart. The model still returns a whole form — the caller
    /// replaces what it has either way — so the work this prompt does is making
    /// "whole form" mean "the same form, with one thing changed" rather than a
    /// fresh take on the same brief.
    /// </summary>
    private const string RefineSystemPrompt = """
        You are editing a web form that already exists. You are given the form's current
        fields as JSON, in exactly the shape you must produce, and a change the user wants
        made. Apply the change and return the complete form — every field it should have
        afterwards, in order — not just the parts that changed.

        """
        + FieldRules
        + """

        Editing rules. Where these conflict with anything above, these win:
        - Return every field the form should still have, including those the change does not
          touch. A field you omit is a field you deleted.
        - A field the request does not concern comes back exactly as given: same name, label,
          type, placeholder, help, validation, options, columnSpan and step. Do not reword,
          reorder, retype or otherwise improve it. Tidying a field nobody asked you to touch
          is a bug, not a courtesy.
        - `name` is what already-submitted data is keyed on. Keep it stable for every field
          that survives, even when you change that field's label.
        - Keep the existing step structure — the same `startsNewStep` and `stepLabel` values —
          unless the change asks for different steps.
        - Add new fields where the request implies they belong; absent any hint, at the end of
          the step they relate to. Re-balance a row's `columnSpan` values only on rows you
          added a field to or removed one from.
        - If the request describes no change to the fields, return the form unchanged.
        """;

    /// <summary>Indented so the current form reads as structure in the prompt, not as one long line.</summary>
    private static readonly JsonSerializerOptions PromptJson = new() { WriteIndented = true };

    public Task<GenerationResult> GenerateAsync(string prompt, CancellationToken cancellationToken)
    {
        if (!TryValidatePrompt(prompt, out var promptError))
        {
            return Task.FromResult(GenerationResult.Fail(promptError));
        }

        return RunAsync(SystemPrompt, prompt, "Generated form", carryOver: null, cancellationToken);
    }

    public Task<GenerationResult> RefineAsync(
        string prompt,
        string currentName,
        JsonElement currentSchema,
        CancellationToken cancellationToken)
    {
        if (!TryValidatePrompt(prompt, out var promptError))
        {
            return Task.FromResult(GenerationResult.Fail(promptError));
        }

        // The current form arrives in a request body like anything else. Gate it on
        // the way in as well as on the way out: the allowlist that guards a save is
        // also what bounds the size and shape of what we put in front of the model.
        if (!FormSchemaValidator.TryValidate(currentSchema, out var schemaError))
        {
            return Task.FromResult(GenerationResult.Fail($"The current form is not valid: {schemaError}"));
        }

        var current = ToGeneratedForm(currentName, currentSchema);
        if (current.Fields.Count == 0)
        {
            return Task.FromResult(GenerationResult.Fail("The current form has no fields to change."));
        }

        var message = $"""
            Current form:
            {JsonSerializer.Serialize(current, PromptJson)}

            Requested change:
            {prompt}
            """;

        // Fall back to the current name, not a generic one: an edit that says
        // nothing about naming should leave the name alone.
        var fallbackName = string.IsNullOrWhiteSpace(currentName) ? "Generated form" : currentName;
        return RunAsync(RefineSystemPrompt, message, fallbackName, BuildCarryOver(currentSchema), cancellationToken);
    }

    /// <summary>
    /// Indexes the form that was sent by field name, so props the model cannot
    /// see can be restored onto the field they came from.
    ///
    /// Names are only unique within a step, so a name used by two steps is
    /// dropped rather than guessed at: restoring one step's row count onto
    /// another step's field would be a quieter bug than losing it.
    /// </summary>
    private static Dictionary<string, JsonElement> BuildCarryOver(JsonElement schema)
    {
        var byName = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var ambiguous = new HashSet<string>(StringComparer.Ordinal);

        void Visit(JsonElement list)
        {
            foreach (var node in list.EnumerateArray())
            {
                if (node.TryGetProperty("children", out var children)
                    && children.ValueKind == JsonValueKind.Array)
                {
                    Visit(children);
                    continue;
                }

                var name = GetString(node, "name");
                if (name.Length == 0) continue;

                if (!byName.TryAdd(name, node))
                {
                    ambiguous.Add(name);
                }
            }
        }

        if (schema.ValueKind == JsonValueKind.Array)
        {
            Visit(schema);
        }

        foreach (var name in ambiguous)
        {
            byName.Remove(name);
        }

        return byName;
    }

    private static bool TryValidatePrompt(string prompt, out string error)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            error = "Prompt is required.";
            return false;
        }

        if (prompt.Length > MaxPromptLength)
        {
            error = $"Prompt may not exceed {MaxPromptLength} characters.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    /// <summary>
    /// The half both entry points share: ask the model, assemble the schema, and
    /// put it through the same gate a hand-built save goes through. Generation and
    /// refinement differ only in the system prompt and the message.
    /// </summary>
    private async Task<GenerationResult> RunAsync(
        string systemPrompt,
        string userMessage,
        string fallbackName,
        IReadOnlyDictionary<string, JsonElement>? carryOver,
        CancellationToken cancellationToken)
    {
        GeneratedForm? generated;
        try
        {
            var response = await client.Messages.Create(
                new MessageCreateParams
                {
                    Model = Model.ClaudeOpus4_8,
                    MaxTokens = 8_000,
                    Thinking = new ThinkingConfigAdaptive(),
                    OutputConfig = new OutputConfig
                    {
                        Effort = Effort.Medium,
                        Format = new JsonOutputFormat { Schema = BuildOutputSchema() },
                    },
                    System = systemPrompt,
                    Messages = [new() { Role = Role.User, Content = userMessage }],
                },
                cancellationToken);

            // A refusal returns a normal 200 with no usable content — check before reading.
            if (response.StopReason == "refusal")
            {
                return GenerationResult.Fail("The request was declined. Try rephrasing the form description.");
            }

            var json = response.Content
                .Select(b => b.Value)
                .OfType<TextBlock>()
                .Select(t => t.Text)
                .FirstOrDefault();

            if (string.IsNullOrWhiteSpace(json))
            {
                return GenerationResult.Fail("The model returned no content.");
            }

            generated = JsonSerializer.Deserialize<GeneratedForm>(json);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Don't leak provider detail (which can include key/account info) to the client.
            logger.LogError(ex, "Form generation failed");
            return GenerationResult.Fail("Form generation failed. Please try again.");
        }

        if (generated is null || generated.Fields.Count == 0)
        {
            return GenerationResult.Fail("The model produced no fields.");
        }

        var schema = BuildFormKitSchema(generated, carryOver);

        // Same gate as a hand-built save. Generated schema gets no special trust.
        if (!FormSchemaValidator.TryValidate(schema, out var error))
        {
            logger.LogWarning("Generated schema failed validation: {Error}", error);
            return GenerationResult.Fail($"The generated form was rejected: {error}");
        }

        var name = string.IsNullOrWhiteSpace(generated.Name) ? fallbackName : generated.Name.Trim();
        return GenerationResult.Ok(name, schema);
    }

    /// <summary>
    /// Inverse of <see cref="BuildFormKitSchema"/>: a stored FormKit schema back
    /// into the flat shape the model reads and writes. Mirrors fromSchema() in
    /// clientapp/src/builder/schemaModel.js — the nesting is unwound into fields
    /// plus step markers, so the model never has to reason about two shapes.
    /// </summary>
    private static GeneratedForm ToGeneratedForm(string name, JsonElement schema)
    {
        var form = new GeneratedForm { Name = name };

        if (schema.ValueKind != JsonValueKind.Array)
        {
            return form;
        }

        var multiStep = schema.EnumerateArray()
            .FirstOrDefault(node => GetString(node, "$formkit") == "multi-step");

        if (multiStep.ValueKind != JsonValueKind.Object)
        {
            foreach (var node in schema.EnumerateArray())
            {
                form.Fields.Add(ToGeneratedField(node));
            }

            return form;
        }

        if (!multiStep.TryGetProperty("children", out var steps) || steps.ValueKind != JsonValueKind.Array)
        {
            return form;
        }

        foreach (var step in steps.EnumerateArray())
        {
            if (!step.TryGetProperty("children", out var fields) || fields.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var isFirstInStep = true;
            foreach (var node in fields.EnumerateArray())
            {
                var field = ToGeneratedField(node);

                // Marked on the first field of every step including the first:
                // BuildFormKitSchema opens a group at the first field regardless,
                // and takes the label from it, so dropping the marker here would
                // lose step one's label on the way back out.
                if (isFirstInStep)
                {
                    field.StartsNewStep = true;
                    field.StepLabel = GetString(step, "label");
                    isFirstInStep = false;
                }

                form.Fields.Add(field);
            }
        }

        return form;
    }

    private static GeneratedField ToGeneratedField(JsonElement node)
    {
        var field = new GeneratedField
        {
            Type = GetString(node, "$formkit"),
            Name = GetString(node, "name"),
            Label = GetString(node, "label"),
            Placeholder = GetString(node, "placeholder"),
            Help = GetString(node, "help"),
            Validation = GetString(node, "validation"),
            // A field with no span predates the feature, or was omitted as terse
            // full width by ToNode. Either way it renders at 12.
            ColumnSpan = node.TryGetProperty("columnSpan", out var span)
                && span.ValueKind == JsonValueKind.Number
                && span.TryGetInt32(out var value)
                    ? value
                    : FullWidthSpan,
        };

        if (node.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in options.EnumerateObject())
            {
                field.Options.Add(new GeneratedOption
                {
                    Value = entry.Name,
                    Label = entry.Value.ValueKind == JsonValueKind.String
                        ? entry.Value.GetString()!
                        : entry.Name,
                });
            }
        }

        return field;
    }

    private static string GetString(JsonElement node, string name) =>
        node.ValueKind == JsonValueKind.Object
        && node.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : string.Empty;

    /// <summary>
    /// Assembles the FormKit schema. Flat when there are no step boundaries;
    /// multi-step > step > fields when there are — the same shape the builder
    /// produces, so generated and hand-built forms are indistinguishable downstream.
    /// </summary>
    private static JsonElement BuildFormKitSchema(
        GeneratedForm generated,
        IReadOnlyDictionary<string, JsonElement>? carryOver = null)
    {
        var groups = new List<(string Label, List<GeneratedField> Fields)>();

        foreach (var field in generated.Fields)
        {
            if (groups.Count == 0 || field.StartsNewStep)
            {
                var stepLabel = Literal(field.StepLabel);
                var label = string.IsNullOrWhiteSpace(stepLabel)
                    ? $"Step {groups.Count + 1}"
                    : stepLabel;
                groups.Add((label, []));
            }

            groups[^1].Fields.Add(field);
        }

        var isMultiStep = groups.Count > 1;

        var nodes = isMultiStep
            ? new List<object>
            {
                new Dictionary<string, object>
                {
                    ["$formkit"] = "multi-step",
                    ["name"] = "steps",
                    ["children"] = groups.Select((g, i) => new Dictionary<string, object>
                    {
                        ["$formkit"] = "step",
                        ["name"] = $"step{i + 1}",
                        ["label"] = g.Label,
                        ["children"] = g.Fields.Select(f => ToNode(f, carryOver)).ToList(),
                    }).ToList(),
                },
            }
            : groups[0].Fields.Select(f => ToNode(f, carryOver)).Cast<object>().ToList();

        return JsonSerializer.SerializeToElement(nodes);
    }

    /// <summary>
    /// Strips a leading "$", which FormKit evaluates as an expression and the
    /// validator therefore rejects. The system prompt already asks for this, but a
    /// price-shaped label ("$50") slipping through would otherwise cost the whole
    /// form — the same defensive stance as the type and span fallbacks.
    /// </summary>
    private static string Literal(string? text) =>
        string.IsNullOrWhiteSpace(text) ? string.Empty : text.TrimStart('$').TrimStart();

    private static Dictionary<string, object> ToNode(
        GeneratedField field,
        IReadOnlyDictionary<string, JsonElement>? carryOver = null)
    {
        var node = new Dictionary<string, object>
        {
            // Fall back rather than emit an unsupported type — the validator would
            // reject it and lose the whole form over one bad field.
            ["$formkit"] = AllowedTypes.Contains(field.Type) ? field.Type : "text",
            ["name"] = field.Name,
            ["label"] = Literal(field.Label),
        };

        if (!string.IsNullOrWhiteSpace(field.Placeholder)) node["placeholder"] = Literal(field.Placeholder);
        if (!string.IsNullOrWhiteSpace(field.Help)) node["help"] = Literal(field.Help);
        if (!string.IsNullOrWhiteSpace(field.Validation)) node["validation"] = field.Validation;

        if (field.Options.Count > 0 && field.Type is "select" or "radio")
        {
            var options = new Dictionary<string, string>();
            foreach (var option in field.Options)
            {
                var value = Literal(option.Value);
                if (string.IsNullOrWhiteSpace(value)) continue;
                options[value] = string.IsNullOrWhiteSpace(option.Label) ? value : Literal(option.Label);
            }

            if (options.Count > 0) node["options"] = options;
        }

        if (field.Type == "textarea") node["rows"] = 4;

        // Same defensive stance as the type fallback above: an out-of-range span
        // would fail validation and cost the whole form over one field's width.
        // Omitted at full width — the client treats a missing span as 12, so
        // this keeps generated schema as terse as a hand-built one.
        var span = AllowedColumnSpans.Contains(field.ColumnSpan) ? field.ColumnSpan : FullWidthSpan;
        if (span != FullWidthSpan) node["columnSpan"] = span;

        ApplyCarriedProps(node, field, carryOver);

        return node;
    }

    /// <summary>
    /// Restores the props in <see cref="CarriedProps"/> from the field of the same
    /// name in the form that was sent, overwriting the defaults set above — a
    /// carried rows=8 must beat the textarea default of 4, or carrying it would
    /// achieve nothing.
    ///
    /// Only when the type is unchanged: these props are type-specific, and moving
    /// a number's `max` onto the text field it was just retyped into would be
    /// meaningless at best.
    /// </summary>
    private static void ApplyCarriedProps(
        Dictionary<string, object> node,
        GeneratedField field,
        IReadOnlyDictionary<string, JsonElement>? carryOver)
    {
        if (carryOver is null || !carryOver.TryGetValue(field.Name, out var original))
        {
            return;
        }

        if (GetString(original, "$formkit") != (node["$formkit"] as string))
        {
            return;
        }

        foreach (var prop in original.EnumerateObject())
        {
            if (CarriedProps.Contains(prop.Name))
            {
                node[prop.Name] = prop.Value;
            }
        }
    }

    /// <summary>
    /// Structured-outputs JSON schema. Every property must appear in `required`
    /// and objects must set `additionalProperties: false` — the model uses ""
    /// or [] for values that don't apply. No recursion is permitted, which is
    /// why the field list is flat.
    /// </summary>
    private static Dictionary<string, JsonElement> BuildOutputSchema()
    {
        var types = string.Join(", ", AllowedTypes.Select(t => $"\"{t}\""));
        var spans = string.Join(", ", AllowedColumnSpans);

        var schema = $$"""
            {
              "type": "object",
              "properties": {
                "name": {
                  "type": "string",
                  "description": "Short human-readable name for the form."
                },
                "fields": {
                  "type": "array",
                  "description": "The form's fields, in display order.",
                  "items": {
                    "type": "object",
                    "properties": {
                      "type": { "type": "string", "enum": [{{types}}] },
                      "name": { "type": "string", "description": "camelCase submitted data key." },
                      "label": { "type": "string" },
                      "placeholder": { "type": "string", "description": "Empty string if not applicable." },
                      "help": { "type": "string", "description": "Empty string if not applicable." },
                      "validation": { "type": "string", "description": "FormKit rules, e.g. required|email. Empty string for none." },
                      "options": {
                        "type": "array",
                        "description": "Only for select and radio; empty array otherwise.",
                        "items": {
                          "type": "object",
                          "properties": {
                            "value": { "type": "string" },
                            "label": { "type": "string" }
                          },
                          "required": ["value", "label"],
                          "additionalProperties": false
                        }
                      },
                      "columnSpan": {
                        "type": "integer",
                        "enum": [{{spans}}],
                        "description": "Width on a 12-column grid: 12 full, 6 half, 4 a third, 3 a quarter. Fields wrap onto a new row when the next no longer fits."
                      },
                      "startsNewStep": { "type": "boolean" },
                      "stepLabel": { "type": "string", "description": "Empty string unless startsNewStep is true." }
                    },
                    "required": [
                      "type", "name", "label", "placeholder", "help",
                      "validation", "options", "columnSpan", "startsNewStep", "stepLabel"
                    ],
                    "additionalProperties": false
                  }
                }
              },
              "required": ["name", "fields"],
              "additionalProperties": false
            }
            """;

        return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(schema)!;
    }
}
