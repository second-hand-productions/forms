using System.Text.Json;
using forms.Models;

namespace forms.Services;

public interface IFormSubmissionStore
{
    /// <summary>All submissions for one form, newest first.</summary>
    Task<IReadOnlyCollection<FormSubmission>> GetByFormAsync(Guid formId, CancellationToken cancellationToken = default);

    Task<FormSubmission?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<FormSubmission> CreateAsync(Guid formId, JsonElement data, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
