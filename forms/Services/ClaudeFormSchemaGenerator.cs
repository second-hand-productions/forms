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

    private const string SystemPrompt = """
        You design web forms. Given a description, produce the fields the form needs.

        Rules:
        - `name` is the submitted data key: camelCase, letters and digits only, unique within a step.
        - `label` is what the user reads. Keep it short and human.
        - `validation` uses FormKit rules joined by "|" — e.g. "required", "required|email",
          "required|length:0,500". Use "" when the field is optional and unconstrained.
        - `options` applies only to select and radio. Leave it empty for every other type.
        - `placeholder` and `help` are optional; use "" when they'd add nothing.
        - Only split into steps when a form is genuinely long (roughly 8+ fields) or has
          distinct phases. Set `startsNewStep` true on the first field of each step and give
          that field a `stepLabel`. For a single-step form set `startsNewStep` false everywhere
          and leave `stepLabel` empty.
        - Prefer the smallest set of fields that actually satisfies the request. Do not invent
          fields the user did not ask for or imply.
        """;

    public async Task<GenerationResult> GenerateAsync(string prompt, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return GenerationResult.Fail("Prompt is required.");
        }

        if (prompt.Length > MaxPromptLength)
        {
            return GenerationResult.Fail($"Prompt may not exceed {MaxPromptLength} characters.");
        }

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
                    System = SystemPrompt,
                    Messages = [new() { Role = Role.User, Content = prompt }],
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

        var schema = BuildFormKitSchema(generated);

        // Same gate as a hand-built save. Generated schema gets no special trust.
        if (!FormSchemaValidator.TryValidate(schema, out var error))
        {
            logger.LogWarning("Generated schema failed validation: {Error}", error);
            return GenerationResult.Fail($"The generated form was rejected: {error}");
        }

        var name = string.IsNullOrWhiteSpace(generated.Name) ? "Generated form" : generated.Name.Trim();
        return GenerationResult.Ok(name, schema);
    }

    /// <summary>
    /// Assembles the FormKit schema. Flat when there are no step boundaries;
    /// multi-step > step > fields when there are — the same shape the builder
    /// produces, so generated and hand-built forms are indistinguishable downstream.
    /// </summary>
    private static JsonElement BuildFormKitSchema(GeneratedForm generated)
    {
        var groups = new List<(string Label, List<GeneratedField> Fields)>();

        foreach (var field in generated.Fields)
        {
            if (groups.Count == 0 || field.StartsNewStep)
            {
                var label = string.IsNullOrWhiteSpace(field.StepLabel)
                    ? $"Step {groups.Count + 1}"
                    : field.StepLabel.Trim();
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
                        ["children"] = g.Fields.Select(ToNode).ToList(),
                    }).ToList(),
                },
            }
            : groups[0].Fields.Select(ToNode).Cast<object>().ToList();

        return JsonSerializer.SerializeToElement(nodes);
    }

    private static Dictionary<string, object> ToNode(GeneratedField field)
    {
        var node = new Dictionary<string, object>
        {
            // Fall back rather than emit an unsupported type — the validator would
            // reject it and lose the whole form over one bad field.
            ["$formkit"] = AllowedTypes.Contains(field.Type) ? field.Type : "text",
            ["name"] = field.Name,
            ["label"] = field.Label,
        };

        if (!string.IsNullOrWhiteSpace(field.Placeholder)) node["placeholder"] = field.Placeholder;
        if (!string.IsNullOrWhiteSpace(field.Help)) node["help"] = field.Help;
        if (!string.IsNullOrWhiteSpace(field.Validation)) node["validation"] = field.Validation;

        if (field.Options.Count > 0 && field.Type is "select" or "radio")
        {
            var options = new Dictionary<string, string>();
            foreach (var option in field.Options)
            {
                if (string.IsNullOrWhiteSpace(option.Value)) continue;
                options[option.Value] = string.IsNullOrWhiteSpace(option.Label) ? option.Value : option.Label;
            }

            if (options.Count > 0) node["options"] = options;
        }

        if (field.Type == "textarea") node["rows"] = 4;

        return node;
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
                      "startsNewStep": { "type": "boolean" },
                      "stepLabel": { "type": "string", "description": "Empty string unless startsNewStep is true." }
                    },
                    "required": [
                      "type", "name", "label", "placeholder", "help",
                      "validation", "options", "startsNewStep", "stepLabel"
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
