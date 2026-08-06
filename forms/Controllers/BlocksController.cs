using System.Text.Json;
using forms.Models;
using forms.Services;
using forms.Validation;
using Microsoft.AspNetCore.Mvc;

namespace forms.Controllers;

// Reusable content blocks (snippets, headers, footers) authors insert into report
// templates. Explicit route so the URL is the "blocks" the client calls.
[ApiController]
[Route("api/blocks")]
public class BlocksController(IBlockStore store, IFormStore forms) : ControllerBase
{
    private const int MaxNameLength = 200;

    // The kinds the editor offers. Deny-by-default, like the template validator's
    // node whitelist — an unknown kind is a client bug, rejected up front.
    private static readonly HashSet<string> AllowedKinds = new(StringComparer.Ordinal)
    {
        "header", "footer", "snippet",
    };

    [HttpGet]
    public async Task<ActionResult<IEnumerable<Block>>> GetAll(CancellationToken cancellationToken) =>
        Ok(await store.GetAllAsync(cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<Block>> Get(Guid id, CancellationToken cancellationToken)
    {
        var block = await store.GetAsync(id, cancellationToken);
        return block is null ? NotFound() : Ok(block);
    }

    [HttpPost]
    public async Task<ActionResult<Block>> Create(
        [FromBody] BlockRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryValidateRequest(request, out var name, out var kind, out var formId, out var content, out var error))
        {
            return BadRequest(new ProblemDetails { Title = "Invalid block", Detail = error });
        }

        if (!await FormRefValidAsync(formId, cancellationToken))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Invalid block",
                Detail = "The selected form does not exist.",
            });
        }

        var created = await store.CreateAsync(name, kind, formId, content, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<Block>> Update(
        Guid id,
        [FromBody] BlockRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryValidateRequest(request, out var name, out var kind, out var formId, out var content, out var error))
        {
            return BadRequest(new ProblemDetails { Title = "Invalid block", Detail = error });
        }

        if (!await FormRefValidAsync(formId, cancellationToken))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Invalid block",
                Detail = "The selected form does not exist.",
            });
        }

        var updated = await store.UpdateAsync(id, name, kind, formId, content, cancellationToken);
        return updated is null ? NotFound() : Ok(updated);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken) =>
        await store.DeleteAsync(id, cancellationToken) ? NoContent() : NotFound();

    // A block's form binding is optional — form-agnostic footers and branding
    // leave it null. When one is supplied it must exist now (a reference to a form
    // that never existed is a client bug), but as with report templates the form
    // may be deleted later and the block tolerates the dangling reference. A null
    // binding is always valid.
    private async Task<bool> FormRefValidAsync(Guid? formId, CancellationToken cancellationToken) =>
        formId is null || await forms.GetAsync(formId.Value, cancellationToken) is not null;

    private static bool TryValidateRequest(
        BlockRequest request,
        out string name,
        out string kind,
        out Guid? formId,
        out JsonElement content,
        out string error)
    {
        name = request.Name?.Trim() ?? string.Empty;
        kind = request.Kind?.Trim() ?? string.Empty;
        formId = null;
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

        if (!AllowedKinds.Contains(kind))
        {
            error = $"Kind must be one of: {string.Join(", ", AllowedKinds)}.";
            return false;
        }

        // A supplied FormId of Guid.Empty is treated as "no form" rather than an
        // error, so the client can send an empty or absent value interchangeably.
        formId = request.FormId is { } id && id != Guid.Empty ? id : null;

        if (request.Content is null)
        {
            error = "Block content is required.";
            return false;
        }

        content = request.Content.Value;
        // A block is a report-content document, validated by the very same
        // deny-by-default rules — so a snippet can never carry a node a template
        // itself couldn't. allowBlockRefs: false additionally refuses a snippet
        // that references another snippet, keeping transclusion one level deep.
        return ReportTemplateValidator.TryValidate(content, allowBlockRefs: false, out error);
    }
}
