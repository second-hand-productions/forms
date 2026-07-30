using System.Text.Json;

namespace forms.Services;

public record ReportGenerationResult(bool Success, string Name, JsonElement Content, string Error)
{
    public static ReportGenerationResult Ok(string name, JsonElement content) =>
        new(true, name, content, string.Empty);

    public static ReportGenerationResult Fail(string error) =>
        new(false, string.Empty, default, error);
}

/// <summary>
/// One merge-field candidate derived from the bound form's schema. Mirrors the
/// client's deriveFields (clientapp/src/report/mockData.js): fields before the
/// first step marker carry a null <paramref name="Step"/>; fields inside a step
/// carry that step's name, so a name repeated across steps stays distinct.
/// </summary>
public record FormFieldRef(string Name, string Label, string? Step);

public interface IReportTemplateGenerator
{
    /// <summary>
    /// Generates a report template (a TipTap document) from a text prompt, an
    /// attached document image/PDF, or both. <paramref name="fields"/> is the
    /// bound form's merge-field vocabulary — the model may reference only these,
    /// and the assembled merge fields bind to them. At least one of
    /// <paramref name="prompt"/> and <paramref name="attachment"/> must carry
    /// content.
    /// </summary>
    Task<ReportGenerationResult> GenerateAsync(
        string prompt,
        GenerationAttachment? attachment,
        IReadOnlyList<FormFieldRef> fields,
        CancellationToken cancellationToken);

    /// <summary>
    /// Applies a change to a report template that already exists, returning the
    /// whole template rather than a diff — the caller replaces what it has, as it
    /// does after a generation. <paramref name="currentContent"/> is untrusted
    /// client input and is validated before it reaches the model.
    /// <paramref name="fields"/> is the bound form's merge-field vocabulary.
    /// </summary>
    Task<ReportGenerationResult> RefineAsync(
        string prompt,
        string currentName,
        JsonElement currentContent,
        IReadOnlyList<FormFieldRef> fields,
        CancellationToken cancellationToken);
}
