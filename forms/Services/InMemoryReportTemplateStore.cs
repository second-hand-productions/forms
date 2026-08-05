using System.Collections.Concurrent;
using System.Text.Json;
using forms.Models;

namespace forms.Services;

/// <summary>
/// POC persistence, mirroring <see cref="InMemoryFormStore"/>. Registered as a
/// singleton, so state survives requests but not restarts. Kept as a no-database
/// fallback and for tests; the app registers <see cref="SqlServerReportTemplateStore"/>
/// by default.
///
/// The interface is async to match the SQL-backed store; these operations are
/// synchronous in-memory, so they return already-completed tasks.
/// </summary>
public class InMemoryReportTemplateStore : IReportTemplateStore
{
    private readonly ConcurrentDictionary<Guid, ReportTemplate> _templates = new();

    public Task<IReadOnlyCollection<ReportTemplate>> GetAllAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyCollection<ReportTemplate>>(
            _templates.Values.OrderByDescending(t => t.UpdatedAt).ToList());

    public Task<ReportTemplate?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_templates.TryGetValue(id, out var template) ? template : null);

    public Task<ReportTemplate> CreateAsync(string name, Guid formId, JsonElement content, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var template = new ReportTemplate
        {
            Id = Guid.NewGuid(),
            Name = name,
            FormId = formId,
            // Clone detaches the element from the request's JsonDocument, which is
            // disposed once the request completes. Without this we'd store a
            // dangling reference and throw on the next read.
            Content = content.Clone(),
            CreatedAt = now,
            UpdatedAt = now,
        };

        _templates[template.Id] = template;
        return Task.FromResult(template);
    }

    public Task<ReportTemplate?> UpdateAsync(Guid id, string name, Guid formId, JsonElement content, CancellationToken cancellationToken = default)
    {
        if (!_templates.TryGetValue(id, out var existing))
        {
            return Task.FromResult<ReportTemplate?>(null);
        }

        existing.Name = name;
        existing.FormId = formId;
        existing.Content = content.Clone();
        existing.UpdatedAt = DateTimeOffset.UtcNow;
        return Task.FromResult<ReportTemplate?>(existing);
    }

    public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_templates.TryRemove(id, out _));
}
