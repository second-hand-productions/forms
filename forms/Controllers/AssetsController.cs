using forms.Models;
using forms.Services;
using forms.Validation;
using Microsoft.AspNetCore.Mvc;

namespace forms.Controllers;

// Uploaded image assets (logos, letterheads, branding) that reports reference by
// id. Explicit route so the URL is the "assets" the client calls.
[ApiController]
[Route("api/assets")]
public class AssetsController(IAssetStore store) : ControllerBase
{
    private const int MaxNameLength = 200;

    [HttpGet]
    public async Task<ActionResult<IEnumerable<Asset>>> GetAll(CancellationToken cancellationToken) =>
        Ok(await store.GetAllAsync(cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<Asset>> Get(Guid id, CancellationToken cancellationToken)
    {
        var asset = await store.GetAsync(id, cancellationToken);
        return asset is null ? NotFound() : Ok(asset);
    }

    /// <summary>
    /// Serves an asset's raw bytes — the target of a report's <c>image</c> node
    /// <c>src</c>. Content is immutable (a changed image is a new upload with a new
    /// id), so it is marked cacheable for a long time.
    /// </summary>
    [HttpGet("{id:guid}/content")]
    public async Task<IActionResult> GetContent(Guid id, CancellationToken cancellationToken)
    {
        var content = await store.GetContentAsync(id, cancellationToken);
        if (content is null)
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "private, max-age=31536000, immutable";
        return File(content.Content, content.MediaType);
    }

    /// <summary>
    /// Uploads an image from base64 data plus its media type (the shape the client
    /// already produces for attachments). The type is allowlisted and the leading
    /// bytes are sniffed to confirm they match — see <see cref="AssetValidator"/>.
    /// </summary>
    // A 5 MB image is ~6.7 MB once base64-encoded; allow headroom under Kestrel's
    // 30 MB default for the JSON envelope.
    [HttpPost]
    [RequestSizeLimit(10_000_000)]
    public async Task<ActionResult<Asset>> Create(
        [FromBody] AssetUploadRequest request,
        CancellationToken cancellationToken)
    {
        var name = request.Name?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            return BadRequest(new ProblemDetails { Title = "Invalid image", Detail = "Name is required." });
        }

        if (name.Length > MaxNameLength)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Invalid image",
                Detail = $"Name may not exceed {MaxNameLength} characters.",
            });
        }

        if (string.IsNullOrWhiteSpace(request.Data) || string.IsNullOrWhiteSpace(request.MediaType))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Invalid image",
                Detail = "Both image data and its media type are required.",
            });
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(request.Data);
        }
        catch (FormatException)
        {
            return BadRequest(new ProblemDetails { Title = "Invalid image", Detail = "The image is not valid base64." });
        }

        var mediaType = request.MediaType.Trim().ToLowerInvariant();
        if (!AssetValidator.TryValidate(mediaType, bytes, out var error))
        {
            return BadRequest(new ProblemDetails { Title = "Invalid image", Detail = error });
        }

        var created = await store.CreateAsync(name, mediaType, bytes, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken) =>
        await store.DeleteAsync(id, cancellationToken) ? NoContent() : NotFound();
}
