using System.Text.Json;
using forms.Models;
using forms.Services;
using forms.Validation;
using Microsoft.AspNetCore.Mvc;

namespace forms.Controllers;

// Explicit route (not the [controller] token) so the URL is the hyphenated
// "report-templates" the client calls, rather than "ReportTemplates".
[ApiController]
[Route("api/report-templates")]
public class ReportTemplatesController(
    IReportTemplateStore store,
    IFormStore forms,
    IReportTemplateGenerator? generator = null) : ControllerBase
{
    private const int MaxNameLength = 200;

    /// <summary>
    /// Generates a report template from a natural-language prompt, an attached
    /// image/PDF of an existing document, or both. The report binds to a form
    /// (<see cref="GenerateReportRequest.FormId"/>) whose fields become the
    /// merge-field vocabulary. Like form generation, the result is returned for
    /// the user to review and edit rather than persisted here.
    /// </summary>
    // A 20 MB PDF is ~27 MB once base64-encoded, past Kestrel's 30 MB default.
    [HttpPost("generate")]
    [RequestSizeLimit(40_000_000)]
    public async Task<IActionResult> Generate(
        [FromBody] GenerateReportRequest request,
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

        if (request.FormId is null || request.FormId == Guid.Empty)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Form required",
                Detail = "Choose the form this report is about before generating.",
            });
        }

        var form = forms.Get(request.FormId.Value);
        if (form is null)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Form not found",
                Detail = "The selected form does not exist.",
            });
        }

        if (!GenerationAttachmentReader.TryRead(
                request.FileData, request.FileMediaType, out var attachment, out var attachmentError))
        {
            return BadRequest(new ProblemDetails { Title = "Invalid attachment", Detail = attachmentError });
        }

        var fields = DeriveFields(form.Schema);

        var result = await generator.GenerateAsync(
            request.Prompt ?? string.Empty, attachment, fields, cancellationToken);

        if (!result.Success)
        {
            return BadRequest(new ProblemDetails { Title = "Generation failed", Detail = result.Error });
        }

        return Ok(new { name = result.Name, formId = request.FormId, content = result.Content });
    }

    /// <summary>
    /// Applies a natural-language change to a report template the caller already
    /// has. Same contract as <see cref="Generate"/> — a whole template back, for
    /// review, not persisted. The difference is upstream: the current template goes
    /// with the prompt, so the model edits it instead of starting over.
    /// </summary>
    [HttpPost("refine")]
    public async Task<IActionResult> Refine(
        [FromBody] RefineReportRequest request,
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

        if (request.FormId is null || request.FormId == Guid.Empty)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Form required",
                Detail = "Choose the form this report is about before refining.",
            });
        }

        var form = forms.Get(request.FormId.Value);
        if (form is null)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Form not found",
                Detail = "The selected form does not exist.",
            });
        }

        if (request.Content is null)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Refinement failed",
                Detail = "The current report is required.",
            });
        }

        var fields = DeriveFields(form.Schema);

        var result = await generator.RefineAsync(
            request.Prompt ?? string.Empty,
            request.Name?.Trim() ?? string.Empty,
            request.Content.Value,
            fields,
            cancellationToken);

        if (!result.Success)
        {
            return BadRequest(new ProblemDetails { Title = "Refinement failed", Detail = result.Error });
        }

        return Ok(new { name = result.Name, formId = request.FormId, content = result.Content });
    }

    /// <summary>
    /// Flattens a saved form's FormKit schema into merge-field candidates, mirroring
    /// the client's deriveFields (clientapp/src/report/mockData.js): a multi-step
    /// schema contributes each step's fields carrying that step's name; a flat
    /// schema contributes fields with a null step. Only real input nodes (those with
    /// a <c>name</c>, excluding the structural multi-step/step nodes) are emitted.
    /// </summary>
    private static List<FormFieldRef> DeriveFields(JsonElement schema)
    {
        var fields = new List<FormFieldRef>();
        if (schema.ValueKind != JsonValueKind.Array)
        {
            return fields;
        }

        var nodes = schema.EnumerateArray().ToList();
        var multiStep = nodes.FirstOrDefault(n =>
            n.ValueKind == JsonValueKind.Object
            && n.TryGetProperty("$formkit", out var t)
            && t.ValueKind == JsonValueKind.String
            && t.GetString() == "multi-step");

        if (multiStep.ValueKind == JsonValueKind.Object && multiStep.TryGetProperty("children", out var steps)
            && steps.ValueKind == JsonValueKind.Array)
        {
            foreach (var step in steps.EnumerateArray())
            {
                var stepName = GetString(step, "name");
                if (step.TryGetProperty("children", out var stepFields)
                    && stepFields.ValueKind == JsonValueKind.Array)
                {
                    foreach (var field in stepFields.EnumerateArray())
                    {
                        AddField(fields, field, stepName);
                    }
                }
            }

            return fields;
        }

        foreach (var node in nodes)
        {
            AddField(fields, node, step: null);
        }

        return fields;
    }

    private static void AddField(List<FormFieldRef> fields, JsonElement node, string? step)
    {
        if (node.ValueKind != JsonValueKind.Object) return;

        var name = GetString(node, "name");
        if (string.IsNullOrWhiteSpace(name)) return;

        var type = GetString(node, "$formkit");
        if (type is "multi-step" or "step") return;

        var label = GetString(node, "label");
        fields.Add(new FormFieldRef(name!, string.IsNullOrWhiteSpace(label) ? name! : label!, step));
    }

    private static string? GetString(JsonElement node, string prop) =>
        node.TryGetProperty(prop, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    [HttpGet]
    public ActionResult<IEnumerable<ReportTemplate>> GetAll() => Ok(store.GetAll());

    [HttpGet("{id:guid}")]
    public ActionResult<ReportTemplate> Get(Guid id)
    {
        var template = store.Get(id);
        return template is null ? NotFound() : Ok(template);
    }

    [HttpPost]
    public ActionResult<ReportTemplate> Create([FromBody] ReportTemplateRequest request)
    {
        if (!TryValidateRequest(request, out var name, out var formId, out var content, out var error))
        {
            return BadRequest(new ProblemDetails { Title = "Invalid report template", Detail = error });
        }

        var created = store.Create(name, formId, content);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpPut("{id:guid}")]
    public ActionResult<ReportTemplate> Update(Guid id, [FromBody] ReportTemplateRequest request)
    {
        if (!TryValidateRequest(request, out var name, out var formId, out var content, out var error))
        {
            return BadRequest(new ProblemDetails { Title = "Invalid report template", Detail = error });
        }

        var updated = store.Update(id, name, formId, content);
        return updated is null ? NotFound() : Ok(updated);
    }

    [HttpDelete("{id:guid}")]
    public IActionResult Delete(Guid id) => store.Delete(id) ? NoContent() : NotFound();

    private bool TryValidateRequest(
        ReportTemplateRequest request,
        out string name,
        out Guid formId,
        out System.Text.Json.JsonElement content,
        out string error)
    {
        name = request.Name?.Trim() ?? string.Empty;
        formId = Guid.Empty;
        content = default;
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

        if (request.FormId is null || request.FormId == Guid.Empty)
        {
            error = "A form must be selected for this report.";
            return false;
        }

        formId = request.FormId.Value;

        // The report binds to a form that exists now. The form may be deleted
        // later — the renderer tolerates that — but a template referencing a
        // form that never existed is a client bug, so reject it up front.
        if (forms.Get(formId) is null)
        {
            error = "The selected form does not exist.";
            return false;
        }

        if (request.Content is null)
        {
            error = "Template content is required.";
            return false;
        }

        content = request.Content.Value;
        return ReportTemplateValidator.TryValidate(content, out error);
    }
}
