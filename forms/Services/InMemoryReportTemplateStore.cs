using System.Collections.Concurrent;
using System.Text.Json;
using forms.Models;

namespace forms.Services;

/// <summary>
/// POC persistence, mirroring <see cref="InMemoryFormStore"/>. Registered as a
/// singleton, so state survives requests but not restarts. Swapping this for a
/// real database means implementing <see cref="IReportTemplateStore"/> and
/// changing one registration in Program.cs.
/// </summary>
public class InMemoryReportTemplateStore : IReportTemplateStore
{
    private readonly ConcurrentDictionary<Guid, ReportTemplate> _templates = new();

    public IReadOnlyCollection<ReportTemplate> GetAll() =>
        _templates.Values.OrderByDescending(t => t.UpdatedAt).ToList();

    public ReportTemplate? Get(Guid id) =>
        _templates.TryGetValue(id, out var template) ? template : null;

    public ReportTemplate Create(string name, Guid formId, JsonElement content)
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
        return template;
    }

    public ReportTemplate? Update(Guid id, string name, Guid formId, JsonElement content)
    {
        if (!_templates.TryGetValue(id, out var existing))
        {
            return null;
        }

        existing.Name = name;
        existing.FormId = formId;
        existing.Content = content.Clone();
        existing.UpdatedAt = DateTimeOffset.UtcNow;
        return existing;
    }

    public bool Delete(Guid id) => _templates.TryRemove(id, out _);
}
