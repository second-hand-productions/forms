using System.Data;
using forms.Models;
using Microsoft.Data.SqlClient;

namespace forms.Services;

/// <summary>
/// SQL Server-backed <see cref="IAssetStore"/>, mirroring the other SQL stores.
/// Image bytes are persisted in a <c>VARBINARY(MAX)</c> <c>Content</c> column.
/// Reads that list or fetch metadata deliberately omit <c>Content</c>, so the
/// bytes travel only through <see cref="GetContentAsync"/> (the serve path).
///
/// Safe to register as a singleton: it holds only a connection string and opens a
/// pooled <see cref="SqlConnection"/> per call, async, with the shared
/// <see cref="SqlRetry"/> transient-fault policy.
///
/// The backing table (created out-of-band, like the others):
/// <code>
/// CREATE TABLE dbo.Assets (
///     Id        UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
///     Name      NVARCHAR(200)    NOT NULL,
///     MediaType NVARCHAR(100)    NOT NULL,
///     SizeBytes BIGINT           NOT NULL,
///     [Content] VARBINARY(MAX)   NOT NULL,
///     CreatedAt DATETIMEOFFSET   NOT NULL
/// );
/// </code>
/// </summary>
public class SqlServerAssetStore : IAssetStore
{
    private readonly string _connectionString;

    public SqlServerAssetStore(string connectionString) => _connectionString = connectionString;

    public async Task<IReadOnlyCollection<Asset>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT Id, Name, MediaType, SizeBytes, CreatedAt
            FROM dbo.Assets
            ORDER BY CreatedAt DESC;
            """;

        await using var conn = await SqlRetry.OpenConnectionAsync(_connectionString, cancellationToken);
        using var cmd = new SqlCommand(sql, conn) { RetryLogicProvider = SqlRetry.Provider };
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        var assets = new List<Asset>();
        while (await reader.ReadAsync(cancellationToken))
        {
            assets.Add(MapMetadata(reader));
        }

        return assets;
    }

    public async Task<Asset?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT Id, Name, MediaType, SizeBytes, CreatedAt
            FROM dbo.Assets
            WHERE Id = @id;
            """;

        await using var conn = await SqlRetry.OpenConnectionAsync(_connectionString, cancellationToken);
        using var cmd = new SqlCommand(sql, conn) { RetryLogicProvider = SqlRetry.Provider };
        cmd.Parameters.AddWithValue("@id", id);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? MapMetadata(reader) : null;
    }

    public async Task<AssetContent?> GetContentAsync(Guid id, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT MediaType, [Content]
            FROM dbo.Assets
            WHERE Id = @id;
            """;

        await using var conn = await SqlRetry.OpenConnectionAsync(_connectionString, cancellationToken);
        using var cmd = new SqlCommand(sql, conn) { RetryLogicProvider = SqlRetry.Provider };
        cmd.Parameters.AddWithValue("@id", id);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new AssetContent(reader.GetString(0), (byte[])reader[1]);
    }

    public async Task<Asset> CreateAsync(string name, string mediaType, byte[] content, CancellationToken cancellationToken = default)
    {
        var asset = new Asset
        {
            Id = Guid.NewGuid(),
            Name = name,
            MediaType = mediaType,
            SizeBytes = content.Length,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        const string sql = """
            INSERT INTO dbo.Assets (Id, Name, MediaType, SizeBytes, [Content], CreatedAt)
            VALUES (@id, @name, @mediaType, @sizeBytes, @content, @createdAt);
            """;

        await using var conn = await SqlRetry.OpenConnectionAsync(_connectionString, cancellationToken);
        using var cmd = new SqlCommand(sql, conn); // write: open-level retry only
        cmd.Parameters.AddWithValue("@id", asset.Id);
        cmd.Parameters.AddWithValue("@name", asset.Name);
        cmd.Parameters.AddWithValue("@mediaType", asset.MediaType);
        cmd.Parameters.AddWithValue("@sizeBytes", asset.SizeBytes);
        // Explicit VarBinary(MAX) rather than AddWithValue, which would infer a
        // sized type and can truncate a large image.
        cmd.Parameters.Add("@content", SqlDbType.VarBinary, -1).Value = content;
        cmd.Parameters.AddWithValue("@createdAt", asset.CreatedAt);
        await cmd.ExecuteNonQueryAsync(cancellationToken);

        return asset;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        const string sql = "DELETE FROM dbo.Assets WHERE Id = @id;";

        await using var conn = await SqlRetry.OpenConnectionAsync(_connectionString, cancellationToken);
        using var cmd = new SqlCommand(sql, conn); // write: open-level retry only
        cmd.Parameters.AddWithValue("@id", id);
        return await cmd.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    private static Asset MapMetadata(SqlDataReader reader) => new()
    {
        Id = reader.GetGuid(0),
        Name = reader.GetString(1),
        MediaType = reader.GetString(2),
        SizeBytes = reader.GetInt64(3),
        CreatedAt = reader.GetDateTimeOffset(4),
    };
}
