namespace forms.Models;

/// <summary>
/// Metadata for an uploaded image asset — a logo, letterhead or other branding
/// image a report references. The bytes live in the store; this is what the list
/// and detail endpoints return as JSON, and what a report's <c>image</c> node
/// points at by <see cref="Id"/>. Serving the bytes is a separate endpoint, so
/// listing the asset library never ships the images themselves.
///
/// A report stores only the asset id, never a URL or the bytes — the same
/// reference-by-id discipline that keeps <c>blockRef</c> and merge fields safe.
/// The renderer builds the <c>src</c> from the id; an id it can't resolve simply
/// renders nothing.
/// </summary>
public class Asset
{
    public Guid Id { get; init; }

    public required string Name { get; set; }

    public required string MediaType { get; set; }

    public required long SizeBytes { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>An asset's bytes and media type, for the serve endpoint.</summary>
public record AssetContent(string MediaType, byte[] Content);

/// <summary>
/// Client upload payload: base64 <see cref="Data"/> plus its <see cref="MediaType"/>,
/// mirroring the shape the client already produces for generation attachments.
/// </summary>
public class AssetUploadRequest
{
    public string? Name { get; set; }

    public string? MediaType { get; set; }

    public string? Data { get; set; }
}
