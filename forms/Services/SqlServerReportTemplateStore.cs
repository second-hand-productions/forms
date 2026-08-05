using System.Text.Json;
using forms.Models;
using Microsoft.Data.SqlClient;

namespace forms.Services;

/// <summary>
/// SQL Server-backed <see cref="IReportTemplateStore"/>, mirroring
/// <see cref="SqlServerFormStore"/>. The TipTap/ProseMirror document is persisted
/// as opaque JSON text in the <c>Content</c> column. <c>FormId</c> is stored
/// without a foreign key, matching the design note on <see cref="ReportTemplate"/>
/// that a template tolerates a dangling reference to a deleted form.
///
/// Safe to register as a singleton: it holds only a connection string and opens a
/// pooled <see cref="SqlConnection"/> per call.
/// </summary>
public class SqlServerReportTemplateStore : IReportTemplateStore
{
    private readonly string _connectionString;

    public SqlServerReportTemplateStore(string connectionString) => _connectionString = connectionString;

    public IReadOnlyCollection<ReportTemplate> GetAll()
    {
        const string sql = """
            SELECT Id, Name, FormId, [Content], CreatedAt, UpdatedAt
            FROM dbo.ReportTemplates
            ORDER BY UpdatedAt DESC;
            """;

        using var conn = new SqlConnection(_connectionString);
        using var cmd = new SqlCommand(sql, conn);
        conn.Open();
        using var reader = cmd.ExecuteReader();

        var templates = new List<ReportTemplate>();
        while (reader.Read())
        {
            templates.Add(Map(reader));
        }

        return templates;
    }

    public ReportTemplate? Get(Guid id)
    {
        const string sql = """
            SELECT Id, Name, FormId, [Content], CreatedAt, UpdatedAt
            FROM dbo.ReportTemplates
            WHERE Id = @id;
            """;

        using var conn = new SqlConnection(_connectionString);
        using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id", id);
        conn.Open();
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public ReportTemplate Create(string name, Guid formId, JsonElement content)
    {
        var now = DateTimeOffset.UtcNow;
        var template = new ReportTemplate
        {
            Id = Guid.NewGuid(),
            Name = name,
            FormId = formId,
            // Clone detaches the element from the request's JsonDocument, which is
            // disposed once the request completes — mirrors InMemoryReportTemplateStore.
            Content = content.Clone(),
            CreatedAt = now,
            UpdatedAt = now,
        };

        const string sql = """
            INSERT INTO dbo.ReportTemplates (Id, Name, FormId, [Content], CreatedAt, UpdatedAt)
            VALUES (@id, @name, @formId, @content, @createdAt, @updatedAt);
            """;

        using var conn = new SqlConnection(_connectionString);
        using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id", template.Id);
        cmd.Parameters.AddWithValue("@name", template.Name);
        cmd.Parameters.AddWithValue("@formId", template.FormId);
        cmd.Parameters.AddWithValue("@content", content.GetRawText());
        cmd.Parameters.AddWithValue("@createdAt", template.CreatedAt);
        cmd.Parameters.AddWithValue("@updatedAt", template.UpdatedAt);
        conn.Open();
        cmd.ExecuteNonQuery();

        return template;
    }

    public ReportTemplate? Update(Guid id, string name, Guid formId, JsonElement content)
    {
        // OUTPUT returns the persisted row (including the untouched CreatedAt) only
        // when a row matched, so a null reader means "not found".
        const string sql = """
            UPDATE dbo.ReportTemplates
            SET Name = @name, FormId = @formId, [Content] = @content, UpdatedAt = @updatedAt
            OUTPUT inserted.Id, inserted.Name, inserted.FormId, inserted.[Content], inserted.CreatedAt, inserted.UpdatedAt
            WHERE Id = @id;
            """;

        using var conn = new SqlConnection(_connectionString);
        using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@formId", formId);
        cmd.Parameters.AddWithValue("@content", content.GetRawText());
        cmd.Parameters.AddWithValue("@updatedAt", DateTimeOffset.UtcNow);
        conn.Open();
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public bool Delete(Guid id)
    {
        const string sql = "DELETE FROM dbo.ReportTemplates WHERE Id = @id;";

        using var conn = new SqlConnection(_connectionString);
        using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id", id);
        conn.Open();
        return cmd.ExecuteNonQuery() > 0;
    }

    private static ReportTemplate Map(SqlDataReader reader)
    {
        using var doc = JsonDocument.Parse(reader.GetString(3));
        return new ReportTemplate
        {
            Id = reader.GetGuid(0),
            Name = reader.GetString(1),
            FormId = reader.GetGuid(2),
            Content = doc.RootElement.Clone(),
            CreatedAt = reader.GetDateTimeOffset(4),
            UpdatedAt = reader.GetDateTimeOffset(5),
        };
    }
}
