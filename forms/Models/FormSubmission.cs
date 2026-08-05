using System.Text.Json;

namespace forms.Models;

/// <summary>
/// A captured response to a form. <see cref="Data"/> is a flat map of merge key →
/// value, keyed exactly as the report renderer looks values up: a field inside a
/// step resolves to <c>step.field</c>, a field in a single-step form to the bare
/// <c>field</c> name (see clientapp/src/report/renderTemplate.js mergeKey). Storing
/// it pre-flattened means a report merges a real submission with the same lookups
/// it used for the sample data it replaced — no per-render reshaping.
///
/// Like <see cref="FormDefinition"/> the payload is opaque JSON, so the form's
/// field shape can evolve without a submission migration. Nothing here assumes the
/// referenced form still exists.
/// </summary>
public class FormSubmission
{
    public Guid Id { get; init; }

    public required Guid FormId { get; set; }

    public required JsonElement Data { get; set; }

    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>Client payload for capturing a submission. Id, FormId and timestamp are server-owned.</summary>
public class FormSubmissionRequest
{
    public JsonElement? Data { get; set; }
}
