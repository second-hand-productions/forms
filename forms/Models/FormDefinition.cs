using System.Text.Json;

namespace forms.Models;

/// <summary>
/// A saved form. <see cref="Schema"/> is the FormKit schema array the client
/// renders — stored as opaque JSON rather than modelled relationally, so the
/// builder and the AI generator can evolve the node shape without migrations.
/// </summary>
public class FormDefinition
{
    public Guid Id { get; init; }

    public required string Name { get; set; }

    public required JsonElement Schema { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Client payload for create/update. Id and timestamps are server-owned.</summary>
public class FormDefinitionRequest
{
    public string? Name { get; set; }

    public JsonElement? Schema { get; set; }
}
