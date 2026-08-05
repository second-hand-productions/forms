using System.Text.Json;
using forms.Models;

namespace forms.Services;

public interface IFormStore
{
    Task<IReadOnlyCollection<FormDefinition>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<FormDefinition?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<FormDefinition> CreateAsync(string name, JsonElement schema, CancellationToken cancellationToken = default);

    Task<FormDefinition?> UpdateAsync(Guid id, string name, JsonElement schema, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
