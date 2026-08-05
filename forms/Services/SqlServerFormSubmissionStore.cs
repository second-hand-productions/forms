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
/// pooled <see cref="SqlConnection"/> per call.
/// </summary>
public class SqlServerFormSubmissionStore : IFormSubmissionStore
{
    private readonly string _connectionString;

    public SqlServerFormSubmissionStore(string connectionString) => _connectionString = connectionString;

    public IReadOnlyCollection<FormSubmission> GetByForm(Guid formId)
    {
        const string sql = """
            SELECT Id, FormId, [Data], CreatedAt
            FROM dbo.FormSubmissions
            WHERE FormId = @formId
            ORDER BY CreatedAt DESC;
            """;

        using var conn = new SqlConnection(_connectionString);
        using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@formId", formId);
        conn.Open();
        using var reader = cmd.ExecuteReader();

        var submissions = new List<FormSubmission>();
        while (reader.Read())
        {
            submissions.Add(Map(reader));
        }

        return submissions;
    }

    public FormSubmission? Get(Guid id)
    {
        const string sql = """
            SELECT Id, FormId, [Data], CreatedAt
            FROM dbo.FormSubmissions
            WHERE Id = @id;
            """;

        using var conn = new SqlConnection(_connectionString);
        using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id", id);
        conn.Open();
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public FormSubmission Create(Guid formId, JsonElement data)
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

        using var conn = new SqlConnection(_connectionString);
        using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id", submission.Id);
        cmd.Parameters.AddWithValue("@formId", submission.FormId);
        cmd.Parameters.AddWithValue("@data", data.GetRawText());
        cmd.Parameters.AddWithValue("@createdAt", submission.CreatedAt);
        conn.Open();
        cmd.ExecuteNonQuery();

        return submission;
    }

    public bool Delete(Guid id)
    {
        const string sql = "DELETE FROM dbo.FormSubmissions WHERE Id = @id;";

        using var conn = new SqlConnection(_connectionString);
        using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id", id);
        conn.Open();
        return cmd.ExecuteNonQuery() > 0;
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
