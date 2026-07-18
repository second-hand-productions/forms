using System.Collections.Concurrent;
using System.Text.Json;
using forms.Models;

namespace forms.Services;

/// <summary>
/// POC persistence. Registered as a singleton, so state survives requests but
/// not restarts. Swapping this for a real database means implementing
/// <see cref="IFormStore"/> and changing one registration in Program.cs.
/// </summary>
public class InMemoryFormStore : IFormStore
{
    private readonly ConcurrentDictionary<Guid, FormDefinition> _forms = new();

    public IReadOnlyCollection<FormDefinition> GetAll() =>
        _forms.Values.OrderByDescending(f => f.UpdatedAt).ToList();

    public FormDefinition? Get(Guid id) => _forms.TryGetValue(id, out var form) ? form : null;

    public FormDefinition Create(string name, JsonElement schema)
    {
        var now = DateTimeOffset.UtcNow;
        var form = new FormDefinition
        {
            Id = Guid.NewGuid(),
            Name = name,
            // Clone detaches the element from the request's JsonDocument, which is
            // disposed once the request completes. Without this we'd store a
            // dangling reference and throw on the next read.
            Schema = schema.Clone(),
            CreatedAt = now,
            UpdatedAt = now,
        };

        _forms[form.Id] = form;
        return form;
    }

    public FormDefinition? Update(Guid id, string name, JsonElement schema)
    {
        if (!_forms.TryGetValue(id, out var existing))
        {
            return null;
        }

        existing.Name = name;
        existing.Schema = schema.Clone();
        existing.UpdatedAt = DateTimeOffset.UtcNow;
        return existing;
    }

    public bool Delete(Guid id) => _forms.TryRemove(id, out _);
}
