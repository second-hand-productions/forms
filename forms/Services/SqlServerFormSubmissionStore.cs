using System.Text.Json;
using forms.Models;
using Microsoft.Data.SqlClient;

namespace forms.Services;

/// <summary>
/// SQL Server-backed <see cref="IFormSubmissionStore"/>, mirroring
/// <see cref="SqlServerFormStore"/>. The captured values are persisted as opaque
/// JSON text in the <c>Data</c> column; <c>FormId</c> is stored without a foreign
/// key, consistent with the rest of the schema's tolerance for a form that is
/// deleted after the fact.
///
/// Safe to register as a singleton: it holds only a connection string and opens a
/// pooled <see cref="SqlConnection"/> per call, async, with the shared
/// <see cref="SqlRetry"/> transient-fault policy.
/// </summary>
public class SqlServerFormSubmissionStore : IFormSubmissionStore
{
    private readonly string _connectionString;

    public SqlServerFormSubmissionStore(string connectionString) => _connectionString = connectionString;

    public async Task<IReadOnlyCollection<FormSubmission>> GetByFormAsync(Guid formId, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT Id, FormId, [Data], CreatedAt
            FROM dbo.FormSubmissions
            WHERE FormId = @formId
            ORDER BY CreatedAt DESC;
            """;

        await using var conn = await SqlRetry.OpenConnectionAsync(_connectionString, cancellationToken);
        using var cmd = new SqlCommand(sql, conn) { RetryLogicProvider = SqlRetry.Provider };
        cmd.Parameters.AddWithValue("@formId", formId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        var submissions = new List<FormSubmission>();
        while (await reader.ReadAsync(cancellationToken))
        {
            submissions.Add(Map(reader));
        }

        return submissions;
    }

    public async Task<FormSubmission?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT Id, FormId, [Data], CreatedAt
            FROM dbo.FormSubmissions
            WHERE Id = @id;
            """;

        await using var conn = await SqlRetry.OpenConnectionAsync(_connectionString, cancellationToken);
        using var cmd = new SqlCommand(sql, conn) { RetryLogicProvider = SqlRetry.Provider };
        cmd.Parameters.AddWithValue("@id", id);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Map(reader) : null;
    }

    public async Task<FormSubmission> CreateAsync(Guid formId, JsonElement data, CancellationToken cancellationToken = default)
    {
        var submission = new FormSubmission
        {
            Id = Guid.NewGuid(),
            FormId = formId,
            Data = data.Clone(),
            CreatedAt = DateTimeOffset.UtcNow,
        };

        const string sql = """
            INSERT INTO dbo.FormSubmissions (Id, FormId, [Data], CreatedAt)
            VALUES (@id, @formId, @data, @createdAt);
            """;

        await using var conn = await SqlRetry.OpenConnectionAsync(_connectionString, cancellationToken);
        using var cmd = new SqlCommand(sql, conn); // write: open-level retry only
        cmd.Parameters.AddWithValue("@id", submission.Id);
        cmd.Parameters.AddWithValue("@formId", submission.FormId);
        cmd.Parameters.AddWithValue("@data", data.GetRawText());
        cmd.Parameters.AddWithValue("@createdAt", submission.CreatedAt);
        await cmd.ExecuteNonQueryAsync(cancellationToken);

        return submission;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        const string sql = "DELETE FROM dbo.FormSubmissions WHERE Id = @id;";

        await using var conn = await SqlRetry.OpenConnectionAsync(_connectionString, cancellationToken);
        using var cmd = new SqlCommand(sql, conn); // write: open-level retry only
        cmd.Parameters.AddWithValue("@id", id);
        return await cmd.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    private static FormSubmission Map(SqlDataReader reader)
    {
        using var doc = JsonDocument.Parse(reader.GetString(2));
        return new FormSubmission
        {
            Id = reader.GetGuid(0),
            FormId = reader.GetGuid(1),
            Data = doc.RootElement.Clone(),
            CreatedAt = reader.GetDateTimeOffset(3),
        };
    }
}
