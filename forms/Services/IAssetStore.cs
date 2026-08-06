using forms.Models;

namespace forms.Services;

public interface IAssetStore
{
    Task<IReadOnlyCollection<Asset>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<Asset?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>The bytes and media type for serving, or null if the asset is gone.</summary>
    Task<AssetContent?> GetContentAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Asset> CreateAsync(string name, string mediaType, byte[] content, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
