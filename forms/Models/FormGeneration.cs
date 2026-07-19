using System.Text.Json.Serialization;

namespace forms.Models;

public class GenerateFormRequest
{
    /// <summary>Natural-language description of the form the user wants.</summary>
    public string? Prompt { get; set; }
}

/// <summary>
/// The shape Claude is constrained to produce via structured outputs.
///
/// Deliberately flat: FormKit's multi-step structure is nested
/// (multi-step > step > fields), but structured outputs don't support recursive
/// schemas, and a flat list maps directly onto the builder's own model — an
/// ordered list of fields with step markers. <see cref="GeneratedField.StartsNewStep"/>
/// is the step boundary; the server assembles the nesting.
/// </summary>
public class GeneratedForm
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("fields")]
    public List<GeneratedField> Fields { get; set; } = [];
}

public class GeneratedField
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    /// <summary>Empty string when not applicable — structured outputs require every property.</summary>
    [JsonPropertyName("placeholder")]
    public string Placeholder { get; set; } = string.Empty;

    [JsonPropertyName("help")]
    public string Help { get; set; } = string.Empty;

    /// <summary>FormKit validation string, e.g. "required|email". Empty for none.</summary>
    [JsonPropertyName("validation")]
    public string Validation { get; set; } = string.Empty;

    /// <summary>Only meaningful for select/radio. Empty otherwise.</summary>
    [JsonPropertyName("options")]
    public List<GeneratedOption> Options { get; set; } = [];

    /// <summary>
    /// How many of the twelve grid columns the field occupies. Fields flow left
    /// to right and wrap when the next one no longer fits, so this is what makes
    /// generated forms lay out in rows rather than one field per line.
    /// </summary>
    [JsonPropertyName("columnSpan")]
    public int ColumnSpan { get; set; } = 12;

    /// <summary>True when this field begins a new step in a multi-step form.</summary>
    [JsonPropertyName("startsNewStep")]
    public bool StartsNewStep { get; set; }

    /// <summary>Tab label for the step this field begins. Empty unless StartsNewStep.</summary>
    [JsonPropertyName("stepLabel")]
    public string StepLabel { get; set; } = string.Empty;
}

public class GeneratedOption
{
    [JsonPropertyName("value")]
    public string Value { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;
}
