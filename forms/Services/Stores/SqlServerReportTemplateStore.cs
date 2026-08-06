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
/// pooled <see cref="SqlConnection"/> per call, async, with the shared
/// <see cref="SqlRetry"/> transient-fault policy.
/// </summary>
public class SqlServerReportTemplateStore : IReportTemplateStore
{
    private readonly string _connectionString;

    public SqlServerReportTemplateStore(string connectionString) => _connectionString = connectionString;

    public async Task<IReadOnlyCollection<ReportTemplate>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT Id, Name, FormId, [Content], CreatedAt, UpdatedAt
            FROM dbo.ReportTemplates
            ORDER BY UpdatedAt DESC;
            """;

        await using var conn = await SqlRetry.OpenConnectionAsync(_connectionString, cancellationToken);
        using var cmd = new SqlCommand(sql, conn) { RetryLogicProvider = SqlRetry.Provider };
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        var templates = new List<ReportTemplate>();
        while (await reader.ReadAsync(cancellationToken))
        {
            templates.Add(Map(reader));
        }

        return templates;
    }

    public async Task<ReportTemplate?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT Id, Name, FormId, [Content], CreatedAt, UpdatedAt
            FROM dbo.ReportTemplates
            WHERE Id = @id;
            """;

        await using var conn = await SqlRetry.OpenConnectionAsync(_connectionString, cancellationToken);
        using var cmd = new SqlCommand(sql, conn) { RetryLogicProvider = SqlRetry.Provider };
        cmd.Parameters.AddWithValue("@id", id);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Map(reader) : null;
    }

    public async Task<ReportTemplate> CreateAsync(string name, Guid formId, JsonElement content, CancellationToken cancellationToken = default)
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

        await using var conn = await SqlRetry.OpenConnectionAsync(_connectionString, cancellationToken);
        using var cmd = new SqlCommand(sql, conn); // write: open-level retry only
        cmd.Parameters.AddWithValue("@id", template.Id);
        cmd.Parameters.AddWithValue("@name", template.Name);
        cmd.Parameters.AddWithValue("@formId", template.FormId);
        cmd.Parameters.AddWithValue("@content", content.GetRawText());
        cmd.Parameters.AddWithValue("@createdAt", template.CreatedAt);
        cmd.Parameters.AddWithValue("@updatedAt", template.UpdatedAt);
        await cmd.ExecuteNonQueryAsync(cancellationToken);

        return template;
    }

    public async Task<ReportTemplate?> UpdateAsync(Guid id, string name, Guid formId, JsonElement content, CancellationToken cancellationToken = default)
    {
        // OUTPUT returns the persisted row (including the untouched CreatedAt) only
        // when a row matched, so a null reader means "not found".
        const string sql = """
            UPDATE dbo.ReportTemplates
            SET Name = @name, FormId = @formId, [Content] = @content, UpdatedAt = @updatedAt
            OUTPUT inserted.Id, inserted.Name, inserted.FormId, inserted.[Content], inserted.CreatedAt, inserted.UpdatedAt
            WHERE Id = @id;
            """;

        await using var conn = await SqlRetry.OpenConnectionAsync(_connectionString, cancellationToken);
        using var cmd = new SqlCommand(sql, conn); // write: open-level retry only
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@formId", formId);
        cmd.Parameters.AddWithValue("@content", content.GetRawText());
        cmd.Parameters.AddWithValue("@updatedAt", DateTimeOffset.UtcNow);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Map(reader) : null;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        const string sql = "DELETE FROM dbo.ReportTemplates WHERE Id = @id;";

        await using var conn = await SqlRetry.OpenConnectionAsync(_connectionString, cancellationToken);
        using var cmd = new SqlCommand(sql, conn); // write: open-level retry only
        cmd.Parameters.AddWithValue("@id", id);
        return await cmd.ExecuteNonQueryAsync(cancellationToken) > 0;
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
