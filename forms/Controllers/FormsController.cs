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
    /// Generates a schema from a natural-language prompt. Returns the schema for
    /// the user to review and edit — it is deliberately not persisted here, so a
    /// generation is a starting point rather than a saved form.
    /// </summary>
    [HttpPost("generate")]
    public async Task<IActionResult> Generate(
        [FromBody] GenerateFormRequest request,
        CancellationToken cancellationToken)
    {
        if (generator is null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new ProblemDetails
            {
                Title = "Generation unavailable",
                Detail = "No Anthropic API key is configured on the server.",
            });
        }

        var result = await generator.GenerateAsync(request.Prompt ?? string.Empty, cancellationToken);

        if (!result.Success)
        {
            return BadRequest(new ProblemDetails { Title = "Generation failed", Detail = result.Error });
        }

        return Ok(new { name = result.Name, schema = result.Schema });
    }

    [HttpGet]
    public ActionResult<IEnumerable<FormDefinition>> GetAll() => Ok(store.GetAll());

    [HttpGet("{id:guid}")]
    public ActionResult<FormDefinition> Get(Guid id)
    {
        var form = store.Get(id);
        return form is null ? NotFound() : Ok(form);
    }

    [HttpPost]
    public ActionResult<FormDefinition> Create([FromBody] FormDefinitionRequest request)
    {
        if (!TryValidateRequest(request, out var name, out var schema, out var error))
        {
            return BadRequest(new ProblemDetails { Title = "Invalid form", Detail = error });
        }

        var created = store.Create(name, schema);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpPut("{id:guid}")]
    public ActionResult<FormDefinition> Update(Guid id, [FromBody] FormDefinitionRequest request)
    {
        if (!TryValidateRequest(request, out var name, out var schema, out var error))
        {
            return BadRequest(new ProblemDetails { Title = "Invalid form", Detail = error });
        }

        var updated = store.Update(id, name, schema);
        return updated is null ? NotFound() : Ok(updated);
    }

    [HttpDelete("{id:guid}")]
    public IActionResult Delete(Guid id) => store.Delete(id) ? NoContent() : NotFound();

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
