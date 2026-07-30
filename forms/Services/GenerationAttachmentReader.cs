namespace forms.Services;

/// <summary>
/// Decodes an optional base64 upload from a generation request into a
/// <see cref="GenerationAttachment"/>. Shared by the form and report generation
/// endpoints so both accept attachments identically.
///
/// Absent file (neither data nor type) is success with a null attachment; a
/// malformed one, or data without its media type (or vice versa), is a
/// validation failure. The type allowlist and size caps live in the generators —
/// this only decodes and normalizes the media type.
/// </summary>
public static class GenerationAttachmentReader
{
    public static bool TryRead(
        string? fileData,
        string? fileMediaType,
        out GenerationAttachment? attachment,
        out string error)
    {
        attachment = null;
        error = string.Empty;

        var hasData = !string.IsNullOrWhiteSpace(fileData);
        var hasType = !string.IsNullOrWhiteSpace(fileMediaType);

        if (!hasData && !hasType)
        {
            return true;
        }

        if (hasData != hasType)
        {
            error = "An attachment needs both file data and its media type.";
            return false;
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(fileData!);
        }
        catch (FormatException)
        {
            error = "The attachment is not valid base64.";
            return false;
        }

        if (bytes.Length == 0)
        {
            error = "The attachment is empty.";
            return false;
        }

        attachment = new GenerationAttachment(bytes, fileMediaType!.Trim().ToLowerInvariant());
        return true;
    }
}
