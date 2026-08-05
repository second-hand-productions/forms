using System.Collections.Concurrent;
using System.Text.Json;
using forms.Models;

namespace forms.Services;

/// <summary>
/// POC persistence. Registered as a singleton, so state survives requests but
/// not restarts. Kept as a no-database fallback and for tests; the app registers
/// <see cref="SqlServerFormStore"/> by default.
///
/// The interface is async to match the SQL-backed store; these operations are
/// synchronous in-memory, so they return already-completed tasks.
/// </summary>
public class InMemoryFormStore : IFormStore
{
    private readonly ConcurrentDictionary<Guid, FormDefinition> _forms = new();

    public Task<IReadOnlyCollection<FormDefinition>> GetAllAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyCollection<FormDefinition>>(
            _forms.Values.OrderByDescending(f => f.UpdatedAt).ToList());

    public Task<FormDefinition?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_forms.TryGetValue(id, out var form) ? form : null);

    public Task<FormDefinition> CreateAsync(string name, JsonElement schema, CancellationToken cancellationToken = default)
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
        return Task.FromResult(form);
    }

    public Task<FormDefinition?> UpdateAsync(Guid id, string name, JsonElement schema, CancellationToken cancellationToken = default)
    {
        if (!_forms.TryGetValue(id, out var existing))
        {
            return Task.FromResult<FormDefinition?>(null);
        }

        existing.Name = name;
        existing.Schema = schema.Clone();
        existing.UpdatedAt = DateTimeOffset.UtcNow;
        return Task.FromResult<FormDefinition?>(existing);
    }

    public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_forms.TryRemove(id, out _));
}
