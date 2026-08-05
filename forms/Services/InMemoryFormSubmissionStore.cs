using System.Collections.Concurrent;
using System.Text.Json;
using forms.Models;

namespace forms.Services;

/// <summary>
/// POC persistence, mirroring <see cref="InMemoryFormStore"/>. Kept as a
/// no-database fallback and for tests; the app registers
/// <see cref="SqlServerFormSubmissionStore"/> by default.
/// </summary>
public class InMemoryFormSubmissionStore : IFormSubmissionStore
{
    private readonly ConcurrentDictionary<Guid, FormSubmission> _submissions = new();

    public IReadOnlyCollection<FormSubmission> GetByForm(Guid formId) =>
        _submissions.Values
            .Where(s => s.FormId == formId)
            .OrderByDescending(s => s.CreatedAt)
            .ToList();

    public FormSubmission? Get(Guid id) =>
        _submissions.TryGetValue(id, out var submission) ? submission : null;

    public FormSubmission Create(Guid formId, JsonElement data)
    {
        var submission = new FormSubmission
        {
            Id = Guid.NewGuid(),
            FormId = formId,
            // Clone detaches the element from the request's JsonDocument, which is
            // disposed once the request completes.
            Data = data.Clone(),
            CreatedAt = DateTimeOffset.UtcNow,
        };

        _submissions[submission.Id] = submission;
        return submission;
    }

    public bool Delete(Guid id) => _submissions.TryRemove(id, out _);
}
