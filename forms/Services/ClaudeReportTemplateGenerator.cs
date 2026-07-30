using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using forms.Models;
using forms.Validation;

namespace forms.Services;

/// <summary>
/// Turns a natural-language prompt and/or an attached document into a report
/// template — the TipTap/ProseMirror document JSON the client renders.
///
/// The same two trust layers as <see cref="ClaudeFormSchemaGenerator"/>:
///   1. Structured outputs constrain Claude to the flat <see cref="GeneratedReport"/>
///      shape, so we never parse free-form prose or hand-written JSON.
///   2. The assembled document still passes through <see cref="ReportTemplateValidator"/>
///      before it is returned. Model output is untrusted input; the allowlist is
///      the security boundary, not the prompt.
///
/// Merge fields are additionally constrained to the bound form's real fields:
/// the model may name only fields we hand it, and any name it invents anyway is
/// dropped rather than emitted as a broken binding.
/// </summary>
public class ClaudeReportTemplateGenerator(
    AnthropicClient client,
    ILogger<ClaudeReportTemplateGenerator> logger) : IReportTemplateGenerator
{
    private const int MaxPromptLength = 2_000;

    private static readonly Dictionary<string, MediaType> ImageMediaTypes = new()
    {
        ["image/png"] = MediaType.ImagePng,
        ["image/jpeg"] = MediaType.ImageJpeg,
        ["image/gif"] = MediaType.ImageGif,
        ["image/webp"] = MediaType.ImageWebP,
    };

    private const string PdfMediaType = "application/pdf";

    private const int MaxImageBytes = 5 * 1024 * 1024;
    private const int MaxPdfBytes = 20 * 1024 * 1024;

    /// <summary>Block types the model may emit, mapped to renderer/validator node types.</summary>
    private static readonly string[] AllowedBlockTypes =
        ["paragraph", "heading", "bulletList", "orderedList"];

    private const int MinHeadingLevel = 1;
    private const int MaxHeadingLevel = 3;

    /// <summary>
    /// The document shape and core rules, shared verbatim by generation and
    /// refinement so a refined template obeys the same conventions as a generated
    /// one — otherwise every edit would drift the template away from house style.
    /// </summary>
    private const string DocumentRules = """
        A template is a formatted document. Where a real submitted value should appear you place a
        "field" run naming the form field to merge in; everywhere else you write literal text. When
        the template is filled, each field run is replaced by that field's submitted value.

        Produce the document as an ordered list of blocks:
        - A "paragraph" or "heading" block carries inline `content`: a sequence of runs. Leave `items` empty.
        - A "heading" block also sets `level` (1, 2 or 3); paragraphs and lists use level 0.
        - A "bulletList" or "orderedList" block carries `items`, each item its own sequence of runs.
          Leave `content` empty on a list block.
        - Each run is either type "text" (with `text`, optionally bold/italic/strike) or type "field"
          (with `fieldName` set to a form field's exact name; leave `text` empty and every mark false).

        Rules:
        - Reference only the fields listed in the user message, by their exact `name`. Never invent a
          field name. If you need a value the form does not capture, write descriptive literal text.
        - Weave fields into natural sentences — "Dear " then a field run for the first name, then "," —
          rather than a bare dump of values, unless the user explicitly asks for a data summary.
        - Keep it concise and professional. Do not fabricate content the user did not ask for.
        """;

    private const string SystemPrompt = """
        You write report and letter templates that merge in data submitted through a web form.

        """
        + DocumentRules
        + """

        - When an image or PDF of an existing document is attached, transcribe its wording and layout,
          replacing the parts that correspond to form fields with field runs. Any text instruction
          refines or overrides what the document shows when they conflict.
        """;

    /// <summary>
    /// The refine counterpart. The model still returns a whole template — the
    /// caller replaces what it has either way — so this prompt's job is to make
    /// "whole template" mean "the same template, with one thing changed" rather
    /// than a fresh take on the same brief.
    /// </summary>
    private const string RefineSystemPrompt = """
        You are editing a report template that already exists. You are given the template's current
        blocks as JSON, in exactly the shape you must produce, and a change the user wants made. Apply
        the change and return the complete template — every block it should have afterwards, in order —
        not just the parts that changed.

        """
        + DocumentRules
        + """

        Editing rules. Where these conflict with anything above, these win:
        - Return every block the template should still have, including those the change does not touch.
          A block you omit is a block you deleted.
        - Text and field runs the request does not concern come back exactly as given — same wording,
          same field names, same order. Rewriting content nobody asked you to touch is a bug, not a
          courtesy.
        - Keep existing field runs bound to the same field `name`. A field's label changing in the
          form does not change the name you reference.
        - Add or remove content only where the request implies it.
        - If the request describes no change to the template, return it unchanged.
        """;

    /// <summary>Indented so the current template reads as structure in the prompt, not one long line.</summary>
    private static readonly JsonSerializerOptions PromptJson = new() { WriteIndented = true };

    public async Task<ReportGenerationResult> GenerateAsync(
        string prompt,
        GenerationAttachment? attachment,
        IReadOnlyList<FormFieldRef> fields,
        CancellationToken cancellationToken)
    {
        var hasPrompt = !string.IsNullOrWhiteSpace(prompt);

        if (!hasPrompt && attachment is null)
        {
            return ReportGenerationResult.Fail("Describe the report, or attach an image or PDF of one.");
        }

        if (hasPrompt && prompt.Length > MaxPromptLength)
        {
            return ReportGenerationResult.Fail($"Prompt may not exceed {MaxPromptLength} characters.");
        }

        var lookup = BuildLookup(fields);

        if (!TryBuildContent(prompt, attachment, fields, out var content, out var contentError))
        {
            return ReportGenerationResult.Fail(contentError);
        }

        return await RunAsync(SystemPrompt, content, lookup, "Generated report", cancellationToken);
    }

    public async Task<ReportGenerationResult> RefineAsync(
        string prompt,
        string currentName,
        JsonElement currentContent,
        IReadOnlyList<FormFieldRef> fields,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return ReportGenerationResult.Fail("Describe the change you want.");
        }

        if (prompt.Length > MaxPromptLength)
        {
            return ReportGenerationResult.Fail($"Prompt may not exceed {MaxPromptLength} characters.");
        }

        // The current template arrives in a request body like anything else. Gate it
        // on the way in as well as on the way out: the allowlist that guards a save
        // is also what bounds the size and shape of what we put before the model.
        if (!ReportTemplateValidator.TryValidate(currentContent, out var currentError))
        {
            return ReportGenerationResult.Fail($"The current report is not valid: {currentError}");
        }

        var lookup = BuildLookup(fields);
        var current = ToGeneratedReport(currentName, currentContent);
        if (current.Blocks.Count == 0)
        {
            return ReportGenerationResult.Fail("The current report has no content to change.");
        }

        var message = new StringBuilder();
        message.AppendLine("Current report template:");
        message.AppendLine(JsonSerializer.Serialize(current, PromptJson));
        message.AppendLine();
        message.AppendLine(BuildFieldCatalogue(fields));
        message.AppendLine();
        message.AppendLine("Requested change:");
        message.Append(prompt);

        var content = new List<ContentBlockParam> { new TextBlockParam { Text = message.ToString() } };

        // Fall back to the current name, not a generic one: an edit that says
        // nothing about naming should leave the name alone.
        var fallbackName = string.IsNullOrWhiteSpace(currentName) ? "Generated report" : currentName;
        return await RunAsync(RefineSystemPrompt, content, lookup, fallbackName, cancellationToken);
    }

    /// <summary>
    /// Indexes the merge-field vocabulary by exact name. First occurrence wins for
    /// a name repeated across steps — a rare case the POC accepts rather than
    /// threading step disambiguation through the model.
    /// </summary>
    private static Dictionary<string, FormFieldRef> BuildLookup(IReadOnlyList<FormFieldRef> fields)
    {
        var lookup = new Dictionary<string, FormFieldRef>(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            if (!string.IsNullOrWhiteSpace(field.Name))
            {
                lookup.TryAdd(field.Name, field);
            }
        }

        return lookup;
    }

    /// <summary>
    /// The half both entry points share: ask the model, assemble the document, and
    /// put it through the same gate a hand-built save goes through. Generation and
    /// refinement differ only in the system prompt and the message content.
    /// </summary>
    private async Task<ReportGenerationResult> RunAsync(
        string systemPrompt,
        List<ContentBlockParam> content,
        Dictionary<string, FormFieldRef> lookup,
        string fallbackName,
        CancellationToken cancellationToken)
    {
        GeneratedReport? generated;
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
                    Messages = [new() { Role = Role.User, Content = content }],
                },
                cancellationToken);

            if (response.StopReason == "refusal")
            {
                return ReportGenerationResult.Fail(
                    "The request was declined. Try rephrasing the report description.");
            }

            var json = response.Content
                .Select(b => b.Value)
                .OfType<TextBlock>()
                .Select(t => t.Text)
                .FirstOrDefault();

            if (string.IsNullOrWhiteSpace(json))
            {
                return ReportGenerationResult.Fail("The model returned no content.");
            }

            generated = JsonSerializer.Deserialize<GeneratedReport>(json);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Don't leak provider detail (which can include key/account info) to the client.
            logger.LogError(ex, "Report generation failed");
            return ReportGenerationResult.Fail("Report generation failed. Please try again.");
        }

        if (generated is null || generated.Blocks.Count == 0)
        {
            return ReportGenerationResult.Fail("The model produced no content.");
        }

        var document = BuildDocument(generated, lookup);

        // Same gate as a hand-built save. Generated content gets no special trust.
        if (!ReportTemplateValidator.TryValidate(document, out var error))
        {
            logger.LogWarning("Generated report failed validation: {Error}", error);
            return ReportGenerationResult.Fail($"The generated report was rejected: {error}");
        }

        var name = string.IsNullOrWhiteSpace(generated.Name) ? fallbackName : generated.Name.Trim();
        return ReportGenerationResult.Ok(name, document);
    }

    /// <summary>
    /// Builds the user message: the attachment (if any) as an image/document block,
    /// then a text block carrying the merge-field catalogue and the user's prompt.
    /// Media type and size are validated here, so an unsupported or oversize file
    /// fails before the provider call.
    /// </summary>
    private static bool TryBuildContent(
        string prompt,
        GenerationAttachment? attachment,
        IReadOnlyList<FormFieldRef> fields,
        out List<ContentBlockParam> content,
        out string error)
    {
        content = [];
        error = string.Empty;

        if (attachment is not null)
        {
            if (attachment.MediaType == PdfMediaType)
            {
                if (attachment.Data.Length > MaxPdfBytes)
                {
                    error = $"The PDF may not exceed {MaxPdfBytes / (1024 * 1024)} MB.";
                    return false;
                }

                content.Add(new DocumentBlockParam
                {
                    Source = new Base64PdfSource { Data = Convert.ToBase64String(attachment.Data) },
                });
            }
            else if (ImageMediaTypes.TryGetValue(attachment.MediaType, out var mediaType))
            {
                if (attachment.Data.Length > MaxImageBytes)
                {
                    error = $"The image may not exceed {MaxImageBytes / (1024 * 1024)} MB.";
                    return false;
                }

                content.Add(new ImageBlockParam
                {
                    Source = new Base64ImageSource
                    {
                        Data = Convert.ToBase64String(attachment.Data),
                        MediaType = mediaType,
                    },
                });
            }
            else
            {
                error = "Attach a PNG, JPEG, GIF or WebP image, or a PDF.";
                return false;
            }
        }

        var text = new StringBuilder();
        text.AppendLine(BuildFieldCatalogue(fields));
        text.AppendLine();
        text.Append(string.IsNullOrWhiteSpace(prompt)
            ? "Recreate this document as a report template, merging in the fields above where they fit."
            : prompt);

        content.Add(new TextBlockParam { Text = text.ToString() });
        return true;
    }

    /// <summary>
    /// The merge-field vocabulary the model may draw on, listed by exact name with
    /// its human label and (for multi-step forms) its step, so the model can pick
    /// the right field and weave it in by name.
    /// </summary>
    private static string BuildFieldCatalogue(IReadOnlyList<FormFieldRef> fields)
    {
        if (fields.Count == 0)
        {
            return "The bound form has no fields, so use literal text only — do not emit any field runs.";
        }

        var sb = new StringBuilder();
        sb.AppendLine("Fields you may merge in (reference by exact name):");
        foreach (var field in fields)
        {
            var label = string.IsNullOrWhiteSpace(field.Label) ? field.Name : field.Label;
            sb.Append("- ").Append(field.Name).Append(" — \"").Append(label).Append('"');
            if (!string.IsNullOrWhiteSpace(field.Step))
            {
                sb.Append(" (step: ").Append(field.Step).Append(')');
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// Inverse of <see cref="BuildDocument"/>: a stored ProseMirror <c>doc</c> back
    /// into the flat block shape the model reads and writes, so a refine shows the
    /// model exactly the shape it must return. A <c>hardBreak</c> — which the flat
    /// shape can't represent — becomes a newline text run; unknown top-level nodes
    /// are skipped.
    /// </summary>
    private static GeneratedReport ToGeneratedReport(string name, JsonElement doc)
    {
        var report = new GeneratedReport { Name = name };

        if (doc.ValueKind != JsonValueKind.Object
            || !doc.TryGetProperty("content", out var blocks)
            || blocks.ValueKind != JsonValueKind.Array)
        {
            return report;
        }

        foreach (var block in blocks.EnumerateArray())
        {
            switch (GetString(block, "type"))
            {
                case "heading":
                    report.Blocks.Add(new GeneratedBlock
                    {
                        Type = "heading",
                        Level = GetHeadingLevel(block),
                        Content = ReadRuns(block),
                    });
                    break;

                case "bulletList":
                case "orderedList":
                    var items = new List<GeneratedListItem>();
                    if (block.TryGetProperty("content", out var listItems)
                        && listItems.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in listItems.EnumerateArray())
                        {
                            items.Add(new GeneratedListItem { Content = ReadItemRuns(item) });
                        }
                    }

                    report.Blocks.Add(new GeneratedBlock { Type = GetString(block, "type"), Items = items });
                    break;

                case "paragraph":
                    report.Blocks.Add(new GeneratedBlock { Type = "paragraph", Content = ReadRuns(block) });
                    break;

                // hardBreak/text can't appear at the top level of a doc; skip anything else.
            }
        }

        return report;
    }

    /// <summary>Reads a block's inline <c>content</c> into text/field runs.</summary>
    private static List<GeneratedRun> ReadRuns(JsonElement node)
    {
        var runs = new List<GeneratedRun>();

        if (!node.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return runs;
        }

        foreach (var inline in content.EnumerateArray())
        {
            switch (GetString(inline, "type"))
            {
                case "mergeField":
                    var fieldName = inline.TryGetProperty("attrs", out var attrs)
                        ? GetString(attrs, "name")
                        : string.Empty;
                    if (fieldName.Length == 0) continue;
                    runs.Add(new GeneratedRun { Type = "field", FieldName = fieldName });
                    break;

                case "text":
                    var text = GetString(inline, "text");
                    if (text.Length == 0) continue;
                    var run = new GeneratedRun { Type = "text", Text = text };
                    if (inline.TryGetProperty("marks", out var marks) && marks.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var mark in marks.EnumerateArray())
                        {
                            switch (GetString(mark, "type"))
                            {
                                case "bold": run.Bold = true; break;
                                case "italic": run.Italic = true; break;
                                case "strike": run.Strike = true; break;
                            }
                        }
                    }

                    runs.Add(run);
                    break;

                case "hardBreak":
                    runs.Add(new GeneratedRun { Type = "text", Text = "\n" });
                    break;
            }
        }

        return runs;
    }

    /// <summary>
    /// A list item wraps its runs in a paragraph (sometimes more than one); gather
    /// the runs from every child so the flat item content matches what the model emits.
    /// </summary>
    private static List<GeneratedRun> ReadItemRuns(JsonElement item)
    {
        var runs = new List<GeneratedRun>();

        if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return runs;
        }

        foreach (var child in content.EnumerateArray())
        {
            runs.AddRange(ReadRuns(child));
        }

        return runs;
    }

    private static int GetHeadingLevel(JsonElement block) =>
        block.TryGetProperty("attrs", out var attrs)
        && attrs.TryGetProperty("level", out var level)
        && level.ValueKind == JsonValueKind.Number
        && level.TryGetInt32(out var value)
            ? value
            : MinHeadingLevel;

    private static string GetString(JsonElement node, string name) =>
        node.ValueKind == JsonValueKind.Object
        && node.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : string.Empty;

    /// <summary>
    /// Assembles the flat block list into a ProseMirror <c>doc</c> — the same node
    /// shape the editor produces and the renderer draws, so a generated template
    /// and a hand-built one are indistinguishable downstream. Empty runs and empty
    /// blocks are dropped; a document that ends up with nothing gets one blank
    /// paragraph so it is still a well-formed, editable doc.
    /// </summary>
    private static JsonElement BuildDocument(GeneratedReport generated, Dictionary<string, FormFieldRef> lookup)
    {
        var blocks = new List<object>();

        foreach (var block in generated.Blocks)
        {
            var type = AllowedBlockTypes.Contains(block.Type) ? block.Type : "paragraph";

            switch (type)
            {
                case "heading":
                {
                    var runs = BuildRuns(block.Content, lookup);
                    if (runs.Count == 0) break;
                    var level = Math.Clamp(block.Level, MinHeadingLevel, MaxHeadingLevel);
                    blocks.Add(new Dictionary<string, object>
                    {
                        ["type"] = "heading",
                        ["attrs"] = new Dictionary<string, object> { ["level"] = level },
                        ["content"] = runs,
                    });
                    break;
                }

                case "bulletList":
                case "orderedList":
                {
                    var items = new List<object>();
                    foreach (var item in block.Items)
                    {
                        var runs = BuildRuns(item.Content, lookup);
                        if (runs.Count == 0) continue;
                        items.Add(new Dictionary<string, object>
                        {
                            ["type"] = "listItem",
                            ["content"] = new List<object>
                            {
                                new Dictionary<string, object> { ["type"] = "paragraph", ["content"] = runs },
                            },
                        });
                    }

                    if (items.Count == 0) break;
                    blocks.Add(new Dictionary<string, object> { ["type"] = type, ["content"] = items });
                    break;
                }

                default:
                {
                    // paragraph — kept even when empty, as a deliberate blank line.
                    var runs = BuildRuns(block.Content, lookup);
                    var node = new Dictionary<string, object> { ["type"] = "paragraph" };
                    if (runs.Count > 0) node["content"] = runs;
                    blocks.Add(node);
                    break;
                }
            }
        }

        if (blocks.Count == 0)
        {
            blocks.Add(new Dictionary<string, object> { ["type"] = "paragraph" });
        }

        var doc = new Dictionary<string, object> { ["type"] = "doc", ["content"] = blocks };
        return JsonSerializer.SerializeToElement(doc);
    }

    private static List<object> BuildRuns(List<GeneratedRun> runs, Dictionary<string, FormFieldRef> lookup)
    {
        var result = new List<object>();

        foreach (var run in runs)
        {
            if (run.Type == "field")
            {
                // Drop invented names rather than emit a binding that renders [missing].
                if (!lookup.TryGetValue(run.FieldName, out var field)) continue;

                var attrs = new Dictionary<string, object> { ["name"] = field.Name };
                if (!string.IsNullOrWhiteSpace(field.Label)) attrs["label"] = field.Label;
                if (!string.IsNullOrWhiteSpace(field.Step)) attrs["step"] = field.Step;

                result.Add(new Dictionary<string, object> { ["type"] = "mergeField", ["attrs"] = attrs });
                continue;
            }

            // ProseMirror text nodes may not be empty; skip a run with no text.
            if (run.Text.Length == 0) continue;

            var node = new Dictionary<string, object> { ["type"] = "text", ["text"] = run.Text };

            var marks = new List<object>();
            if (run.Bold) marks.Add(new Dictionary<string, object> { ["type"] = "bold" });
            if (run.Italic) marks.Add(new Dictionary<string, object> { ["type"] = "italic" });
            if (run.Strike) marks.Add(new Dictionary<string, object> { ["type"] = "strike" });
            if (marks.Count > 0) node["marks"] = marks;

            result.Add(node);
        }

        return result;
    }

    /// <summary>
    /// Structured-outputs JSON schema. Every property appears in <c>required</c>
    /// and objects set <c>additionalProperties: false</c>; the model uses "", 0 or
    /// [] for values that don't apply. No recursion, which is why blocks/items/runs
    /// are a fixed three-level shape rather than the document's true nesting.
    /// </summary>
    private static Dictionary<string, JsonElement> BuildOutputSchema()
    {
        const string run = """
            {
              "type": "object",
              "properties": {
                "type": { "type": "string", "enum": ["text", "field"] },
                "text": { "type": "string", "description": "Literal text for a text run; empty for a field run." },
                "bold": { "type": "boolean" },
                "italic": { "type": "boolean" },
                "strike": { "type": "boolean" },
                "fieldName": { "type": "string", "description": "A form field's exact name for a field run; empty for a text run." }
              },
              "required": ["type", "text", "bold", "italic", "strike", "fieldName"],
              "additionalProperties": false
            }
            """;

        var schema = $$"""
            {
              "type": "object",
              "properties": {
                "name": { "type": "string", "description": "Short human-readable name for the report." },
                "blocks": {
                  "type": "array",
                  "description": "The document's blocks, in display order.",
                  "items": {
                    "type": "object",
                    "properties": {
                      "type": { "type": "string", "enum": ["paragraph", "heading", "bulletList", "orderedList"] },
                      "level": { "type": "integer", "enum": [0, 1, 2, 3], "description": "Heading level 1-3; 0 for non-headings." },
                      "content": { "type": "array", "description": "Inline runs for paragraph/heading; empty array for lists.", "items": {{run}} },
                      "items": {
                        "type": "array",
                        "description": "List items for bullet/ordered lists; empty array otherwise.",
                        "items": {
                          "type": "object",
                          "properties": { "content": { "type": "array", "items": {{run}} } },
                          "required": ["content"],
                          "additionalProperties": false
                        }
                      }
                    },
                    "required": ["type", "level", "content", "items"],
                    "additionalProperties": false
                  }
                }
              },
              "required": ["name", "blocks"],
              "additionalProperties": false
            }
            """;

        return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(schema)!;
    }
}
