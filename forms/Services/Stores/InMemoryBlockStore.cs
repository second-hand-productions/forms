using System.Collections.Concurrent;
using System.Text.Json;
using forms.Models;

namespace forms.Services;

/// <summary>
/// POC persistence, mirroring <see cref="InMemoryReportTemplateStore"/>. Registered
/// as a singleton, so state survives requests but not restarts. Kept as a
/// no-database fallback and for tests; the app registers
/// <see cref="SqlServerBlockStore"/> by default.
///
/// The interface is async to match the SQL-backed store; these operations are
/// synchronous in-memory, so they return already-completed tasks.
/// </summary>
public class InMemoryBlockStore : IBlockStore
{
    private readonly ConcurrentDictionary<Guid, Block> _blocks = new();

    public Task<IReadOnlyCollection<Block>> GetAllAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyCollection<Block>>(
            _blocks.Values.OrderByDescending(b => b.UpdatedAt).ToList());

    public Task<Block?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_blocks.TryGetValue(id, out var block) ? block : null);

    public Task<Block> CreateAsync(string name, string kind, Guid? formId, JsonElement content, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var block = new Block
        {
            Id = Guid.NewGuid(),
            Name = name,
            Kind = kind,
            FormId = formId,
            // Clone detaches the element from the request's JsonDocument, which is
            // disposed once the request completes. Without this we'd store a
            // dangling reference and throw on the next read.
            Content = content.Clone(),
            CreatedAt = now,
            UpdatedAt = now,
        };

        _blocks[block.Id] = block;
        return Task.FromResult(block);
    }

    public Task<Block?> UpdateAsync(Guid id, string name, string kind, Guid? formId, JsonElement content, CancellationToken cancellationToken = default)
    {
        if (!_blocks.TryGetValue(id, out var existing))
        {
            return Task.FromResult<Block?>(null);
        }

        existing.Name = name;
        existing.Kind = kind;
        existing.FormId = formId;
        existing.Content = content.Clone();
        existing.UpdatedAt = DateTimeOffset.UtcNow;
        return Task.FromResult<Block?>(existing);
    }

    public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_blocks.TryRemove(id, out _));
}
