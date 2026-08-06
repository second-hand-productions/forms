using System.Text.Json;
using forms.Models;

namespace forms.Services;

public interface IReportTemplateStore
{
    Task<IReadOnlyCollection<ReportTemplate>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<ReportTemplate?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<ReportTemplate> CreateAsync(string name, Guid formId, JsonElement content, CancellationToken cancellationToken = default);

    Task<ReportTemplate?> UpdateAsync(Guid id, string name, Guid formId, JsonElement content, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
