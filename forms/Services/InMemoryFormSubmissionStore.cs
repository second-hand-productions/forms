using System.Collections.Concurrent;
using System.Text.Json;
using forms.Models;

namespace forms.Services;

/// <summary>
/// POC persistence, mirroring <see cref="InMemoryFormStore"/>. Kept as a
/// no-database fallback and for tests; the app registers
/// <see cref="SqlServerFormSubmissionStore"/> by default.
///
/// The interface is async to match the SQL-backed store; these operations are
/// synchronous in-memory, so they return already-completed tasks.
/// </summary>
public class InMemoryFormSubmissionStore : IFormSubmissionStore
{
    private readonly ConcurrentDictionary<Guid, FormSubmission> _submissions = new();

    public Task<IReadOnlyCollection<FormSubmission>> GetByFormAsync(Guid formId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyCollection<FormSubmission>>(
            _submissions.Values
                .Where(s => s.FormId == formId)
                .OrderByDescending(s => s.CreatedAt)
                .ToList());

    public Task<FormSubmission?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_submissions.TryGetValue(id, out var submission) ? submission : null);

    public Task<FormSubmission> CreateAsync(Guid formId, JsonElement data, CancellationToken cancellationToken = default)
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
        return Task.FromResult(submission);
    }

    public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_submissions.TryRemove(id, out _));
}
