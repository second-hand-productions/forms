using System.Text.Json;
using forms.Models;
using forms.Services;
using Microsoft.AspNetCore.Mvc;

namespace forms.Controllers;

/// <summary>
/// Captured responses to a form. Nested under the form the submission belongs to,
/// so the form id is unambiguous and every route is scoped to one form. The report
/// builder reads these to render a template against real data instead of the
/// sample values it falls back to.
/// </summary>
[ApiController]
[Route("api/forms/{formId:guid}/submissions")]
public class FormSubmissionsController(IFormSubmissionStore store, IFormStore forms) : ControllerBase
{
    [HttpGet]
    public ActionResult<IEnumerable<FormSubmission>> GetAll(Guid formId)
    {
        // A submission list is only meaningful for a form that exists; a missing
        // form is a 404 rather than a silently empty list, which would read as
        // "no responses yet" for a form that was actually deleted.
        if (forms.Get(formId) is null)
        {
            return NotFound();
        }

        return Ok(store.GetByForm(formId));
    }

    [HttpGet("{id:guid}")]
    public ActionResult<FormSubmission> Get(Guid formId, Guid id)
    {
        var submission = store.Get(id);
        return submission is null || submission.FormId != formId ? NotFound() : Ok(submission);
    }

    [HttpPost]
    public ActionResult<FormSubmission> Create(Guid formId, [FromBody] FormSubmissionRequest request)
    {
        if (forms.Get(formId) is null)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Form not found",
                Detail = "Cannot capture a response for a form that does not exist.",
            });
        }

        // Data is the flat merge-key map the renderer consumes — it must be a JSON
        // object. Anything else (an array, a bare string) would never resolve a
        // single merge field, so reject it up front rather than store a payload no
        // report can read.
        if (request.Data is not { ValueKind: JsonValueKind.Object } data)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Invalid submission",
                Detail = "Submission data is required and must be a JSON object of field values.",
            });
        }

        var created = store.Create(formId, data);
        return CreatedAtAction(nameof(Get), new { formId, id = created.Id }, created);
    }

    [HttpDelete("{id:guid}")]
    public IActionResult Delete(Guid formId, Guid id)
    {
        var submission = store.Get(id);
        if (submission is null || submission.FormId != formId)
        {
            return NotFound();
        }

        return store.Delete(id) ? NoContent() : NotFound();
    }
}
