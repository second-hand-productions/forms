using System.Text.Json;
using forms.Models;

namespace forms.Services;

public interface IReportTemplateStore
{
    IReadOnlyCollection<ReportTemplate> GetAll();

    ReportTemplate? Get(Guid id);

    ReportTemplate Create(string name, Guid formId, JsonElement content);

    ReportTemplate? Update(Guid id, string name, Guid formId, JsonElement content);

    bool Delete(Guid id);
}
