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
    public async Task<ActionResult<IEnumerable<FormSubmission>>> GetAll(Guid formId, CancellationToken cancellationToken)
    {
        // A submission list is only meaningful for a form that exists; a missing
        // form is a 404 rather than a silently empty list, which would read as
        // "no responses yet" for a form that was actually deleted.
        if (await forms.GetAsync(formId, cancellationToken) is null)
        {
            return NotFound();
        }

        return Ok(await store.GetByFormAsync(formId, cancellationToken));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<FormSubmission>> Get(Guid formId, Guid id, CancellationToken cancellationToken)
    {
        var submission = await store.GetAsync(id, cancellationToken);
        return submission is null || submission.FormId != formId ? NotFound() : Ok(submission);
    }

    [HttpPost]
    public async Task<ActionResult<FormSubmission>> Create(
        Guid formId,
        [FromBody] FormSubmissionRequest request,
        CancellationToken cancellationToken)
    {
        if (await forms.GetAsync(formId, cancellationToken) is null)
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

        var created = await store.CreateAsync(formId, data, cancellationToken);
        return CreatedAtAction(nameof(Get), new { formId, id = created.Id }, created);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid formId, Guid id, CancellationToken cancellationToken)
    {
        var submission = await store.GetAsync(id, cancellationToken);
        if (submission is null || submission.FormId != formId)
        {
            return NotFound();
        }

        return await store.DeleteAsync(id, cancellationToken) ? NoContent() : NotFound();
    }
}
