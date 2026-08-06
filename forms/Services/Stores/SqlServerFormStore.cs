using System.Text.Json;
using forms.Models;
using Microsoft.Data.SqlClient;

namespace forms.Services;

/// <summary>
/// SQL Server-backed <see cref="IFormStore"/>. The FormKit schema is persisted as
/// opaque JSON text in the <c>Schema</c> column, matching the "evolve the node
/// shape without migrations" design of <see cref="FormDefinition"/>.
///
/// Safe to register as a singleton: it holds only a connection string and opens a
/// pooled <see cref="SqlConnection"/> per call. All I/O is async, and connections
/// open with the shared <see cref="SqlRetry"/> transient-fault policy.
/// </summary>
public class SqlServerFormStore : IFormStore
{
    private readonly string _connectionString;

    public SqlServerFormStore(string connectionString) => _connectionString = connectionString;

    public async Task<IReadOnlyCollection<FormDefinition>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT Id, Name, [Schema], CreatedAt, UpdatedAt
            FROM dbo.FormDefinitions
            ORDER BY UpdatedAt DESC;
            """;

        await using var conn = await SqlRetry.OpenConnectionAsync(_connectionString, cancellationToken);
        // A SELECT is safe to retry at the command level, not just on open.
        using var cmd = new SqlCommand(sql, conn) { RetryLogicProvider = SqlRetry.Provider };
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        var forms = new List<FormDefinition>();
        while (await reader.ReadAsync(cancellationToken))
        {
            forms.Add(Map(reader));
        }

        return forms;
    }

    public async Task<FormDefinition?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT Id, Name, [Schema], CreatedAt, UpdatedAt
            FROM dbo.FormDefinitions
            WHERE Id = @id;
            """;

        await using var conn = await SqlRetry.OpenConnectionAsync(_connectionString, cancellationToken);
        using var cmd = new SqlCommand(sql, conn) { RetryLogicProvider = SqlRetry.Provider };
        cmd.Parameters.AddWithValue("@id", id);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Map(reader) : null;
    }

    public async Task<FormDefinition> CreateAsync(string name, JsonElement schema, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var form = new FormDefinition
        {
            Id = Guid.NewGuid(),
            Name = name,
            // Clone detaches the element from the request's JsonDocument, which is
            // disposed once the request completes — mirrors InMemoryFormStore.
            Schema = schema.Clone(),
            CreatedAt = now,
            UpdatedAt = now,
        };

        const string sql = """
            INSERT INTO dbo.FormDefinitions (Id, Name, [Schema], CreatedAt, UpdatedAt)
            VALUES (@id, @name, @schema, @createdAt, @updatedAt);
            """;

        await using var conn = await SqlRetry.OpenConnectionAsync(_connectionString, cancellationToken);
        // No command-level retry on writes: retry lives on the connection open only,
        // so a transient fault can never replay this INSERT.
        using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id", form.Id);
        cmd.Parameters.AddWithValue("@name", form.Name);
        cmd.Parameters.AddWithValue("@schema", schema.GetRawText());
        cmd.Parameters.AddWithValue("@createdAt", form.CreatedAt);
        cmd.Parameters.AddWithValue("@updatedAt", form.UpdatedAt);
        await cmd.ExecuteNonQueryAsync(cancellationToken);

        return form;
    }

    public async Task<FormDefinition?> UpdateAsync(Guid id, string name, JsonElement schema, CancellationToken cancellationToken = default)
    {
        // OUTPUT returns the persisted row (including the untouched CreatedAt) only
        // when a row matched, so a null reader means "not found" — same contract as
        // the in-memory store.
        const string sql = """
            UPDATE dbo.FormDefinitions
            SET Name = @name, [Schema] = @schema, UpdatedAt = @updatedAt
            OUTPUT inserted.Id, inserted.Name, inserted.[Schema], inserted.CreatedAt, inserted.UpdatedAt
            WHERE Id = @id;
            """;

        await using var conn = await SqlRetry.OpenConnectionAsync(_connectionString, cancellationToken);
        using var cmd = new SqlCommand(sql, conn); // write: open-level retry only
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@schema", schema.GetRawText());
        cmd.Parameters.AddWithValue("@updatedAt", DateTimeOffset.UtcNow);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Map(reader) : null;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        const string sql = "DELETE FROM dbo.FormDefinitions WHERE Id = @id;";

        await using var conn = await SqlRetry.OpenConnectionAsync(_connectionString, cancellationToken);
        using var cmd = new SqlCommand(sql, conn); // write: open-level retry only
        cmd.Parameters.AddWithValue("@id", id);
        return await cmd.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    private static FormDefinition Map(SqlDataReader reader)
    {
        // Parse-then-Clone detaches the element from the JsonDocument so it can be
        // disposed here, exactly as the store returns a self-contained value.
        using var doc = JsonDocument.Parse(reader.GetString(2));
        return new FormDefinition
        {
            Id = reader.GetGuid(0),
            Name = reader.GetString(1),
            Schema = doc.RootElement.Clone(),
            CreatedAt = reader.GetDateTimeOffset(3),
            UpdatedAt = reader.GetDateTimeOffset(4),
        };
    }
}
