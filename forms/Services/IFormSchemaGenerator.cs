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
}
