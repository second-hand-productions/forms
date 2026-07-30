using System.Text.Json;

namespace forms.Models;

/// <summary>
/// A saved report template. <see cref="Content"/> is the TipTap/ProseMirror
/// document JSON the client renders — stored as opaque JSON, like
/// <see cref="FormDefinition.Schema"/>, so the editor can evolve the node shape
/// without migrations.
///
/// A template is authored against one form (<see cref="FormId"/>): its merge
/// fields bind to that form's field names. The form may later be edited or
/// deleted, so nothing here assumes the referenced form still exists — the
/// renderer tolerates a dangling reference.
/// </summary>
public class ReportTemplate
{
    public Guid Id { get; init; }

    public required string Name { get; set; }

    public required Guid FormId { get; set; }

    public required JsonElement Content { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Client payload for create/update. Id and timestamps are server-owned.</summary>
public class ReportTemplateRequest
{
    public string? Name { get; set; }

    public Guid? FormId { get; set; }

    public JsonElement? Content { get; set; }
}
