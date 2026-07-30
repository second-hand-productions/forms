using System.Text.Json;
using System.Text.Json.Serialization;

namespace forms.Models;

public class GenerateReportRequest
{
    /// <summary>Natural-language description of the report the user wants.</summary>
    public string? Prompt { get; set; }

    /// <summary>
    /// Base64-encoded image or PDF of an existing report or letter to transcribe.
    /// Optional — when present the model recreates the document it sees, and
    /// <see cref="Prompt"/> then refines rather than describes. Raw base64 only,
    /// with no data-URL prefix; <see cref="FileMediaType"/> carries the type.
    /// </summary>
    public string? FileData { get; set; }

    /// <summary>
    /// MIME type of <see cref="FileData"/>: image/png, image/jpeg, image/gif,
    /// image/webp, or application/pdf. Required when FileData is present.
    /// </summary>
    public string? FileMediaType { get; set; }

    /// <summary>
    /// The form the report binds to. Its fields become the merge-field vocabulary
    /// the model may reference, so a report cannot be generated without it.
    /// </summary>
    public Guid? FormId { get; set; }
}

/// <summary>
/// A change to make to a report template that already exists.
///
/// Carries the current template as well as the instruction: generation is
/// stateless — nothing is persisted until save — so the only way the model can
/// edit rather than replace is for the client to send back what it currently has.
/// The bound form travels too, since its fields are the merge-field vocabulary the
/// edit may draw on.
/// </summary>
public class RefineReportRequest
{
    /// <summary>Natural-language description of the change, e.g. "add a closing signature".</summary>
    public string? Prompt { get; set; }

    /// <summary>The report's current name, returned unchanged unless the prompt asks otherwise.</summary>
    public string? Name { get; set; }

    /// <summary>The form the report binds to — its fields are the merge-field vocabulary.</summary>
    public Guid? FormId { get; set; }

    /// <summary>The template as it stands, the same TipTap document a save would send.</summary>
    public JsonElement? Content { get; set; }
}

/// <summary>
/// The shape Claude is constrained to produce via structured outputs.
///
/// Deliberately flat, for the same reason as <see cref="GeneratedForm"/>: a
/// TipTap/ProseMirror document is recursively nested, but structured outputs
/// don't support recursive schemas. So the model emits an ordered list of
/// blocks, each carrying either inline content (paragraph/heading) or list items
/// (bullet/ordered), and the server assembles the ProseMirror nesting the
/// editor and renderer expect.
/// </summary>
public class GeneratedReport
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("blocks")]
    public List<GeneratedBlock> Blocks { get; set; } = [];
}

public class GeneratedBlock
{
    /// <summary>One of: paragraph, heading, bulletList, orderedList.</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>Heading level 1–3. Ignored (use 0) for non-heading blocks.</summary>
    [JsonPropertyName("level")]
    public int Level { get; set; }

    /// <summary>Inline runs for a paragraph or heading. Empty for list blocks.</summary>
    [JsonPropertyName("content")]
    public List<GeneratedRun> Content { get; set; } = [];

    /// <summary>List items for a bullet/ordered list. Empty for non-list blocks.</summary>
    [JsonPropertyName("items")]
    public List<GeneratedListItem> Items { get; set; } = [];
}

public class GeneratedListItem
{
    [JsonPropertyName("content")]
    public List<GeneratedRun> Content { get; set; } = [];
}

/// <summary>
/// One inline run: either literal <c>text</c> (optionally emphasized) or a
/// <c>field</c> reference that binds to a form field by <see cref="FieldName"/>.
/// </summary>
public class GeneratedRun
{
    /// <summary>"text" or "field".</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>Literal text for a text run. Empty for a field run.</summary>
    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("bold")]
    public bool Bold { get; set; }

    [JsonPropertyName("italic")]
    public bool Italic { get; set; }

    [JsonPropertyName("strike")]
    public bool Strike { get; set; }

    /// <summary>
    /// For a field run, the <c>name</c> of the form field to merge in. Must match
    /// one of the bound form's fields; an unrecognized name is dropped rather than
    /// emitted as a broken binding. Empty for a text run.
    /// </summary>
    [JsonPropertyName("fieldName")]
    public string FieldName { get; set; } = string.Empty;
}
