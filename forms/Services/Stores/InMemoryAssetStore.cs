using System.Collections.Concurrent;
using forms.Models;

namespace forms.Services;

/// <summary>
/// POC persistence, mirroring the other in-memory stores. Registered as a
/// singleton, so state survives requests but not restarts. Kept as a no-database
/// fallback and for tests; the app registers <see cref="SqlServerAssetStore"/> by
/// default.
///
/// Holds the bytes alongside the metadata; <see cref="GetAllAsync"/> and
/// <see cref="GetAsync"/> project the metadata out so callers never receive the
/// content unless they ask for it via <see cref="GetContentAsync"/>.
/// </summary>
public class InMemoryAssetStore : IAssetStore
{
    private sealed record Stored(Asset Asset, byte[] Content);

    private readonly ConcurrentDictionary<Guid, Stored> _assets = new();

    public Task<IReadOnlyCollection<Asset>> GetAllAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyCollection<Asset>>(
            _assets.Values.Select(s => s.Asset).OrderByDescending(a => a.CreatedAt).ToList());

    public Task<Asset?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_assets.TryGetValue(id, out var stored) ? stored.Asset : null);

    public Task<AssetContent?> GetContentAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_assets.TryGetValue(id, out var stored)
            ? new AssetContent(stored.Asset.MediaType, stored.Content)
            : null);

    public Task<Asset> CreateAsync(string name, string mediaType, byte[] content, CancellationToken cancellationToken = default)
    {
        var asset = new Asset
        {
            Id = Guid.NewGuid(),
            Name = name,
            MediaType = mediaType,
            SizeBytes = content.Length,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        _assets[asset.Id] = new Stored(asset, content);
        return Task.FromResult(asset);
    }

    public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_assets.TryRemove(id, out _));
}
