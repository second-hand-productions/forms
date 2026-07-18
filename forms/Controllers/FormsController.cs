using forms.Models;
using forms.Services;
using forms.Validation;
using Microsoft.AspNetCore.Mvc;

namespace forms.Controllers;

[ApiController]
[Route("api/[controller]")]
public class FormsController(IFormStore store) : ControllerBase
{
    private const int MaxNameLength = 200;

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
