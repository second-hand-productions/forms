using System.Text.Json;
using forms.Models;

namespace forms.Services;

public interface IBlockStore
{
    Task<IReadOnlyCollection<Block>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<Block?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Block> CreateAsync(string name, string kind, Guid? formId, JsonElement content, CancellationToken cancellationToken = default);

    Task<Block?> UpdateAsync(Guid id, string name, string kind, Guid? formId, JsonElement content, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
