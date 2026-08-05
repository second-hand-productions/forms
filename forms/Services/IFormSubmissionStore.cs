using System.Text.Json;
using forms.Models;

namespace forms.Services;

public interface IFormSubmissionStore
{
    /// <summary>All submissions for one form, newest first.</summary>
    IReadOnlyCollection<FormSubmission> GetByForm(Guid formId);

    FormSubmission? Get(Guid id);

    FormSubmission Create(Guid formId, JsonElement data);

    bool Delete(Guid id);
}
