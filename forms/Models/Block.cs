using System.Text.Json;

namespace forms.Models;

/// <summary>
/// A reusable content block — a saved fragment of report content (a client-info
/// header, a footer, a boilerplate snippet) that authors drop into report
/// templates. <see cref="Content"/> is the same TipTap/ProseMirror document JSON
/// as <see cref="ReportTemplate.Content"/>, validated by the same
/// <c>ReportTemplateValidator</c> and stored opaque so the editor's node shape
/// can evolve without migrations.
///
/// Phase 1 reuse is copy-in: inserting a block splices its nodes into the target
/// template, so nothing here is referenced at render time. A later phase can add
/// live references (a <c>blockRef</c> node) on top of this same entity.
///
/// <see cref="FormId"/> is optional. A block that carries merge fields is scoped
/// to the form whose field names those merges bind to; a form-agnostic block
/// (footer, branding) leaves it null. As with <see cref="ReportTemplate"/> the
/// reference is not enforced — a block tolerates a dangling form reference.
/// </summary>
public class Block
{
    public Guid Id { get; init; }

    public required string Name { get; set; }

    /// <summary>"header", "footer" or "snippet" — the picker groups by this.</summary>
    public required string Kind { get; set; }

    public Guid? FormId { get; set; }

    public required JsonElement Content { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Client payload for create/update. Id and timestamps are server-owned.</summary>
public class BlockRequest
{
    public string? Name { get; set; }

    public string? Kind { get; set; }

    public Guid? FormId { get; set; }

    public JsonElement? Content { get; set; }
}
