namespace forms.Validation;

/// <summary>
/// Validates an uploaded image before it is stored. Like the schema and template
/// validators this is a security boundary: the bytes are later served back and
/// rendered in other users' browsers, so we accept only a small allowlist of
/// raster image types, cap the size, and sniff the leading bytes to confirm the
/// content actually matches its declared media type — a file can't claim to be a
/// PNG while carrying something else.
///
/// SVG is deliberately excluded: it is an XML document that can carry script and
/// external references, so serving it would reintroduce exactly the injection
/// surface the rest of the pipeline is built to avoid.
/// </summary>
public static class AssetValidator
{
    /// <summary>Allowed media types and their per-file size cap, in megabytes.</summary>
    private static readonly IReadOnlyDictionary<string, int> Limits = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["image/png"] = 5,
        ["image/jpeg"] = 5,
        ["image/gif"] = 5,
        ["image/webp"] = 5,
    };

    public static IReadOnlyCollection<string> AllowedMediaTypes => (IReadOnlyCollection<string>)Limits.Keys;

    public static bool TryValidate(string? mediaType, byte[] bytes, out string error)
    {
        error = string.Empty;

        var type = mediaType?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!Limits.TryGetValue(type, out var maxMb))
        {
            error = $"Unsupported image type. Allowed: {string.Join(", ", Limits.Keys)}.";
            return false;
        }

        if (bytes.Length == 0)
        {
            error = "The image is empty.";
            return false;
        }

        if (bytes.Length > maxMb * 1024 * 1024)
        {
            error = $"That image is over {maxMb} MB.";
            return false;
        }

        if (!MatchesSignature(type, bytes))
        {
            error = "The image content does not match its declared type.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Confirms the leading bytes are the magic number for the declared type.
    /// Guards against a payload mislabelled with an allowed media type.
    /// </summary>
    private static bool MatchesSignature(string type, byte[] b) => type switch
    {
        // 89 50 4E 47 0D 0A 1A 0A
        "image/png" => b.Length >= 8
            && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47
            && b[4] == 0x0D && b[5] == 0x0A && b[6] == 0x1A && b[7] == 0x0A,
        // FF D8 FF
        "image/jpeg" => b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF,
        // "GIF87a" or "GIF89a"
        "image/gif" => b.Length >= 6
            && b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x38
            && (b[4] == 0x37 || b[4] == 0x39) && b[5] == 0x61,
        // "RIFF" .... "WEBP"
        "image/webp" => b.Length >= 12
            && b[0] == 0x52 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x46
            && b[8] == 0x57 && b[9] == 0x45 && b[10] == 0x42 && b[11] == 0x50,
        _ => false,
    };
}
