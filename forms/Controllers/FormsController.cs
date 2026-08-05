using forms.Models;
using forms.Services;
using forms.Validation;
using Microsoft.AspNetCore.Mvc;

namespace forms.Controllers;

[ApiController]
[Route("api/[controller]")]
public class FormsController(IFormStore store, IFormSchemaGenerator? generator = null) : ControllerBase
{
    private const int MaxNameLength = 200;

    /// <summary>
    /// Generates a schema from a natural-language prompt, an attached image/PDF of
    /// an existing form, or both. Returns the schema for the user to review and
    /// edit — it is deliberately not persisted here, so a generation is a starting
    /// point rather than a saved form.
    /// </summary>
    // Attachments push the JSON body past Kestrel's 30 MB default (a 20 MB PDF is
    // ~27 MB once base64-encoded), so this endpoint alone is allowed more.
    [HttpPost("generate")]
    [RequestSizeLimit(40_000_000)]
    public async Task<IActionResult> Generate(
        [FromBody] GenerateFormRequest request,
        CancellationToken cancellationToken)
    {
        if (generator is null)
        {
            return GenerationUnavailable();
        }

        if (!GenerationAttachmentReader.TryRead(
                request.FileData, request.FileMediaType, out var attachment, out var attachmentError))
        {
            return BadRequest(new ProblemDetails { Title = "Invalid attachment", Detail = attachmentError });
        }

        var result = await generator.GenerateAsync(
            request.Prompt ?? string.Empty, attachment, cancellationToken);

        if (!result.Success)
        {
            return BadRequest(new ProblemDetails { Title = "Generation failed", Detail = result.Error });
        }

        return Ok(new { name = result.Name, schema = result.Schema });
    }

    /// <summary>
    /// Applies a natural-language change to a form the caller already has.
    ///
    /// Same contract as <see cref="Generate"/> — a whole form back, for review,
    /// not persisted. The difference is upstream: the current schema goes with the
    /// prompt, so the model edits it instead of starting over.
    /// </summary>
    [HttpPost("refine")]
    public async Task<IActionResult> Refine(
        [FromBody] RefineFormRequest request,
        CancellationToken cancellationToken)
    {
        if (generator is null)
        {
            return GenerationUnavailable();
        }

        if (request.Schema is null)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Refinement failed",
                Detail = "The current schema is required.",
            });
        }

        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length > MaxNameLength)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Refinement failed",
                Detail = $"Name may not exceed {MaxNameLength} characters.",
            });
        }

        var result = await generator.RefineAsync(
            request.Prompt ?? string.Empty,
            name,
            request.Schema.Value,
            cancellationToken);

        if (!result.Success)
        {
            return BadRequest(new ProblemDetails { Title = "Refinement failed", Detail = result.Error });
        }

        return Ok(new { name = result.Name, schema = result.Schema });
    }

    private ObjectResult GenerationUnavailable() =>
        StatusCode(StatusCodes.Status503ServiceUnavailable, new ProblemDetails
        {
            Title = "Generation unavailable",
            Detail = "No Anthropic API key is configured on the server.",
        });

    [HttpGet]
    public async Task<ActionResult<IEnumerable<FormDefinition>>> GetAll(CancellationToken cancellationToken) =>
        Ok(await store.GetAllAsync(cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<FormDefinition>> Get(Guid id, CancellationToken cancellationToken)
    {
        var form = await store.GetAsync(id, cancellationToken);
        return form is null ? NotFound() : Ok(form);
    }

    [HttpPost]
    public async Task<ActionResult<FormDefinition>> Create(
        [FromBody] FormDefinitionRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryValidateRequest(request, out var name, out var schema, out var error))
        {
            return BadRequest(new ProblemDetails { Title = "Invalid form", Detail = error });
        }

        var created = await store.CreateAsync(name, schema, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<FormDefinition>> Update(
        Guid id,
        [FromBody] FormDefinitionRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryValidateRequest(request, out var name, out var schema, out var error))
        {
            return BadRequest(new ProblemDetails { Title = "Invalid form", Detail = error });
        }

        var updated = await store.UpdateAsync(id, name, schema, cancellationToken);
        return updated is null ? NotFound() : Ok(updated);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken) =>
        await store.DeleteAsync(id, cancellationToken) ? NoContent() : NotFound();

    private static bool TryValidateRequest(
        FormDefinitionRequest request,
        out string name,
        out System.Text.Json.JsonElement schema,
        out string error)
    {
        name = request.Name?.Trim() ?? string.Empty;
        schema = default;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(name))
        {
            error = "Name is required.";
            return false;
        }

        if (name.Length > MaxNameLength)
        {
            error = $"Name may not exceed {MaxNameLength} characters.";
            return false;
        }

        if (request.Schema is null)
        {
            error = "Schema is required.";
            return false;
        }

        schema = request.Schema.Value;
        return FormSchemaValidator.TryValidate(schema, out error);
    }
}
