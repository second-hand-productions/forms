using System.Text.Json;
using forms.Models;
using Microsoft.Data.SqlClient;

namespace forms.Services;

/// <summary>
/// SQL Server-backed <see cref="IBlockStore"/>, mirroring
/// <see cref="SqlServerReportTemplateStore"/>. The TipTap/ProseMirror document is
/// persisted as opaque JSON text in the <c>Content</c> column. <c>FormId</c> is
/// nullable (form-agnostic blocks leave it null) and stored without a foreign
/// key, matching the design note on <see cref="Block"/> that a block tolerates a
/// dangling reference to a deleted form.
///
/// Safe to register as a singleton: it holds only a connection string and opens a
/// pooled <see cref="SqlConnection"/> per call, async, with the shared
/// <see cref="SqlRetry"/> transient-fault policy.
///
/// The backing table (created out-of-band, like the others):
/// <code>
/// CREATE TABLE dbo.Blocks(
///     Id UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
///     Name NVARCHAR(200)    NOT NULL,
///     Kind NVARCHAR(20)     NOT NULL,
///     FormId UNIQUEIDENTIFIER NULL,
///     [Content] NVARCHAR(MAX)    NOT NULL,
///     CreatedAt DATETIMEOFFSET   NOT NULL,
///     UpdatedAt DATETIMEOFFSET   NOT NULL
/// );
/// </code>
/// </summary>
public class SqlServerBlockStore : IBlockStore
{
    private readonly string _connectionString;

    public SqlServerBlockStore(string connectionString) => _connectionString = connectionString;

    public async Task<IReadOnlyCollection<Block>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT Id, Name, Kind, FormId, [Content], CreatedAt, UpdatedAt
            FROM dbo.Blocks
            ORDER BY UpdatedAt DESC;
            """;

        await using var conn = await SqlRetry.OpenConnectionAsync(_connectionString, cancellationToken);
        using var cmd = new SqlCommand(sql, conn) { RetryLogicProvider = SqlRetry.Provider };
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        var blocks = new List<Block>();
        while (await reader.ReadAsync(cancellationToken))
        {
            blocks.Add(Map(reader));
        }

        return blocks;
    }

    public async Task<Block?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT Id, Name, Kind, FormId, [Content], CreatedAt, UpdatedAt
            FROM dbo.Blocks
            WHERE Id = @id;
            """;

        await using var conn = await SqlRetry.OpenConnectionAsync(_connectionString, cancellationToken);
        using var cmd = new SqlCommand(sql, conn) { RetryLogicProvider = SqlRetry.Provider };
        cmd.Parameters.AddWithValue("@id", id);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Map(reader) : null;
    }

    public async Task<Block> CreateAsync(string name, string kind, Guid? formId, JsonElement content, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var block = new Block
        {
            Id = Guid.NewGuid(),
            Name = name,
            Kind = kind,
            FormId = formId,
            // Clone detaches the element from the request's JsonDocument, which is
            // disposed once the request completes — mirrors InMemoryBlockStore.
            Content = content.Clone(),
            CreatedAt = now,
            UpdatedAt = now,
        };

        const string sql = """
            INSERT INTO dbo.Blocks (Id, Name, Kind, FormId, [Content], CreatedAt, UpdatedAt)
            VALUES (@id, @name, @kind, @formId, @content, @createdAt, @updatedAt);
            """;

        await using var conn = await SqlRetry.OpenConnectionAsync(_connectionString, cancellationToken);
        using var cmd = new SqlCommand(sql, conn); // write: open-level retry only
        cmd.Parameters.AddWithValue("@id", block.Id);
        cmd.Parameters.AddWithValue("@name", block.Name);
        cmd.Parameters.AddWithValue("@kind", block.Kind);
        cmd.Parameters.AddWithValue("@formId", (object?)block.FormId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@content", content.GetRawText());
        cmd.Parameters.AddWithValue("@createdAt", block.CreatedAt);
        cmd.Parameters.AddWithValue("@updatedAt", block.UpdatedAt);
        await cmd.ExecuteNonQueryAsync(cancellationToken);

        return block;
    }

    public async Task<Block?> UpdateAsync(Guid id, string name, string kind, Guid? formId, JsonElement content, CancellationToken cancellationToken = default)
    {
        // OUTPUT returns the persisted row (including the untouched CreatedAt) only
        // when a row matched, so a null reader means "not found".
        const string sql = """
            UPDATE dbo.Blocks
            SET Name = @name, Kind = @kind, FormId = @formId, [Content] = @content, UpdatedAt = @updatedAt
            OUTPUT inserted.Id, inserted.Name, inserted.Kind, inserted.FormId, inserted.[Content], inserted.CreatedAt, inserted.UpdatedAt
            WHERE Id = @id;
            """;

        await using var conn = await SqlRetry.OpenConnectionAsync(_connectionString, cancellationToken);
        using var cmd = new SqlCommand(sql, conn); // write: open-level retry only
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@kind", kind);
        cmd.Parameters.AddWithValue("@formId", (object?)formId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@content", content.GetRawText());
        cmd.Parameters.AddWithValue("@updatedAt", DateTimeOffset.UtcNow);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Map(reader) : null;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        const string sql = "DELETE FROM dbo.Blocks WHERE Id = @id;";

        await using var conn = await SqlRetry.OpenConnectionAsync(_connectionString, cancellationToken);
        using var cmd = new SqlCommand(sql, conn); // write: open-level retry only
        cmd.Parameters.AddWithValue("@id", id);
        return await cmd.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    private static Block Map(SqlDataReader reader)
    {
        using var doc = JsonDocument.Parse(reader.GetString(4));
        return new Block
        {
            Id = reader.GetGuid(0),
            Name = reader.GetString(1),
            Kind = reader.GetString(2),
            FormId = reader.IsDBNull(3) ? null : reader.GetGuid(3),
            Content = doc.RootElement.Clone(),
            CreatedAt = reader.GetDateTimeOffset(5),
            UpdatedAt = reader.GetDateTimeOffset(6),
        };
    }
}
