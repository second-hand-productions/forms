using forms.Models;

namespace forms.Services;

public interface IFormStore
{
    IReadOnlyCollection<FormDefinition> GetAll();

    FormDefinition? Get(Guid id);

    FormDefinition Create(string name, System.Text.Json.JsonElement schema);

    FormDefinition? Update(Guid id, string name, System.Text.Json.JsonElement schema);

    bool Delete(Guid id);
}
