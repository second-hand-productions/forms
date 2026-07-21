using System.Text.Json;

namespace forms.Services;

public record GenerationResult(bool Success, string Name, JsonElement Schema, string Error)
{
    public static GenerationResult Ok(string name, JsonElement schema) =>
        new(true, name, schema, string.Empty);

    public static GenerationResult Fail(string error) =>
        new(false, string.Empty, default, error);
}

public interface IFormSchemaGenerator
{
    Task<GenerationResult> GenerateAsync(string prompt, CancellationToken cancellationToken);

    /// <summary>
    /// Applies a change to a form that already exists, returning the whole form
    /// rather than a diff — the caller replaces what it has, as it does after a
    /// generation. <paramref name="currentSchema"/> is untrusted client input and
    /// is validated before it reaches the model.
    /// </summary>
    Task<GenerationResult> RefineAsync(
        string prompt,
        string currentName,
        JsonElement currentSchema,
        CancellationToken cancellationToken);
}
