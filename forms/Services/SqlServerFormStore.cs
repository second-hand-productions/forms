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
/// pooled <see cref="SqlConnection"/> per call. The interface is synchronous, so
/// the ADO.NET calls are too.
/// </summary>
public class SqlServerFormStore : IFormStore
{
    private readonly string _connectionString;

    public SqlServerFormStore(string connectionString) => _connectionString = connectionString;

    public IReadOnlyCollection<FormDefinition> GetAll()
    {
        const string sql = """
            SELECT Id, Name, [Schema], CreatedAt, UpdatedAt
            FROM dbo.FormDefinitions
            ORDER BY UpdatedAt DESC;
            """;

        using var conn = new SqlConnection(_connectionString);
        using var cmd = new SqlCommand(sql, conn);
        conn.Open();
        using var reader = cmd.ExecuteReader();

        var forms = new List<FormDefinition>();
        while (reader.Read())
        {
            forms.Add(Map(reader));
        }

        return forms;
    }

    public FormDefinition? Get(Guid id)
    {
        const string sql = """
            SELECT Id, Name, [Schema], CreatedAt, UpdatedAt
            FROM dbo.FormDefinitions
            WHERE Id = @id;
            """;

        using var conn = new SqlConnection(_connectionString);
        using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id", id);
        conn.Open();
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public FormDefinition Create(string name, JsonElement schema)
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

        using var conn = new SqlConnection(_connectionString);
        using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id", form.Id);
        cmd.Parameters.AddWithValue("@name", form.Name);
        cmd.Parameters.AddWithValue("@schema", schema.GetRawText());
        cmd.Parameters.AddWithValue("@createdAt", form.CreatedAt);
        cmd.Parameters.AddWithValue("@updatedAt", form.UpdatedAt);
        conn.Open();
        cmd.ExecuteNonQuery();

        return form;
    }

    public FormDefinition? Update(Guid id, string name, JsonElement schema)
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

        using var conn = new SqlConnection(_connectionString);
        using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@schema", schema.GetRawText());
        cmd.Parameters.AddWithValue("@updatedAt", DateTimeOffset.UtcNow);
        conn.Open();
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public bool Delete(Guid id)
    {
        const string sql = "DELETE FROM dbo.FormDefinitions WHERE Id = @id;";

        using var conn = new SqlConnection(_connectionString);
        using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id", id);
        conn.Open();
        return cmd.ExecuteNonQuery() > 0;
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
