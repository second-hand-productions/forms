using System.Text.Json;

namespace forms.Validation;

/// <summary>
/// Validates a report template's TipTap/ProseMirror document before it is
/// persisted.
///
/// Like <see cref="FormSchemaValidator"/> this is a security boundary, not a
/// convenience check. Templates arrive from the browser editor and are rendered
/// as HTML in other users' browsers, so we accept only a known-good subset of
/// node and mark types (deny-by-default) and bound the document on three axes —
/// depth, total node count, and per-value string length — so a deeply or widely
/// nested payload cannot exhaust server or client resources.
///
/// The renderer never injects raw HTML: text and merge values become DOM text
/// nodes, escaped by construction. So there is no <c>$</c>-expression hazard as
/// in FormKit; the checks here are about structure and size, not evaluation.
///
/// <para><b>blockRef.</b> A <c>blockRef</c> node is a live reference to a reusable
/// <see cref="forms.Models.Block"/>, resolved and rendered inline at render time.
/// It is accepted only when <c>allowBlockRefs</c> is set — true for report
/// templates, false for blocks themselves. Rejecting it inside a block is what
/// keeps transclusion one level deep: a block cannot reference another block, so
/// there are no reference cycles and the client resolves the reference map in a
/// single fetch pass.</para>
/// </summary>
public static class ReportTemplateValidator
{
    private const int MaxNodes = 1_000;
    private const int MaxStringLength = 20_000;
    private const int MaxAttrStringLength = 200;

    /// <summary>doc &gt; list &gt; listItem &gt; paragraph &gt; text, plus room for a
    /// few levels of nested lists. Nothing legitimate goes deeper.</summary>
    private const int MaxDepth = 20;

    /// <summary>
    /// Node types the editor can produce and the renderer knows how to draw.
    /// <c>blockRef</c> is deliberately absent: it is allowed conditionally (see the
    /// type check in <c>TryValidateNode</c>), gated on <c>allowBlockRefs</c>.
    /// </summary>
    private static readonly HashSet<string> AllowedNodeTypes = new(StringComparer.Ordinal)
    {
        "doc", "paragraph", "text", "heading", "hardBreak",
        "bulletList", "orderedList", "listItem", "mergeField", "image",
    };

    /// <summary>Inline formatting marks permitted on text nodes.</summary>
    private static readonly HashSet<string> AllowedMarkTypes = new(StringComparer.Ordinal)
    {
        "bold", "italic", "strike",
    };

    /// <summary>Top-level properties a node may carry. Anything else is rejected.</summary>
    private static readonly HashSet<string> AllowedNodeProps = new(StringComparer.Ordinal)
    {
        "type", "content", "attrs", "marks", "text",
    };

    private const int MinHeadingLevel = 1;
    private const int MaxHeadingLevel = 3;

    /// <param name="allowBlockRefs">
    /// Whether <c>blockRef</c> (live snippet reference) nodes are permitted. True
    /// for report templates; false for block content, so a block cannot reference
    /// another block.
    /// </param>
    public static bool TryValidate(JsonElement content, bool allowBlockRefs, out string error)
    {
        error = string.Empty;

        if (content.ValueKind != JsonValueKind.Object)
        {
            error = "Template content must be a document object.";
            return false;
        }

        if (!content.TryGetProperty("type", out var rootType)
            || rootType.ValueKind != JsonValueKind.String
            || rootType.GetString() != "doc")
        {
            error = "Template content must be a \"doc\" node.";
            return false;
        }

        // Budget is shared across the whole tree, not per level, so nesting
        // cannot multiply the effective node allowance.
        var budget = MaxNodes;
        return TryValidateNode(content, path: "doc", depth: 1, allowBlockRefs, ref budget, out error);
    }

    private static bool TryValidateNode(
        JsonElement node,
        string path,
        int depth,
        bool allowBlockRefs,
        ref int budget,
        out string error)
    {
        error = string.Empty;

        if (depth > MaxDepth)
        {
            error = $"Template nesting exceeds the maximum depth of {MaxDepth}.";
            return false;
        }

        if (--budget < 0)
        {
            error = $"Template exceeds the maximum of {MaxNodes} nodes.";
            return false;
        }

        if (node.ValueKind != JsonValueKind.Object)
        {
            error = $"Node {path} must be an object.";
            return false;
        }

        if (!node.TryGetProperty("type", out var typeProp)
            || typeProp.ValueKind != JsonValueKind.String)
        {
            error = $"Node {path} must have a string \"type\".";
            return false;
        }

        var type = typeProp.GetString()!;
        if (type == "blockRef")
        {
            // A live snippet reference. Permitted in templates, refused inside a
            // block — that refusal is what keeps transclusion one level deep.
            if (!allowBlockRefs)
            {
                error = $"Node {path} is a snippet reference, which a snippet may "
                    + "not itself contain — detach it first.";
                return false;
            }
        }
        else if (!AllowedNodeTypes.Contains(type))
        {
            error = $"Node {path} uses unsupported type \"{type}\".";
            return false;
        }

        foreach (var prop in node.EnumerateObject())
        {
            if (!AllowedNodeProps.Contains(prop.Name))
            {
                error = $"Node {path} (\"{type}\") has unsupported property \"{prop.Name}\".";
                return false;
            }
        }

        // text: the only node carrying a literal string; must not have children.
        if (type == "text")
        {
            if (!node.TryGetProperty("text", out var textProp) || textProp.ValueKind != JsonValueKind.String)
            {
                error = $"Node {path} (\"text\") must have a string \"text\".";
                return false;
            }

            if (textProp.GetString()!.Length > MaxStringLength)
            {
                error = $"Node {path} (\"text\") exceeds {MaxStringLength} characters.";
                return false;
            }

            if (node.TryGetProperty("content", out _))
            {
                error = $"Node {path} (\"text\") may not have child content.";
                return false;
            }

            return TryValidateMarks(node, path, out error);
        }

        // Non-text nodes carry no literal text.
        if (node.TryGetProperty("text", out _))
        {
            error = $"Node {path} (\"{type}\") may not have a \"text\" property.";
            return false;
        }

        // Marks belong on text nodes only.
        if (node.TryGetProperty("marks", out _))
        {
            error = $"Node {path} (\"{type}\") may not carry marks.";
            return false;
        }

        // blockRef and image are atomic — a blockRef's content comes from the
        // referenced block, an image is a leaf — so neither carries child content.
        if ((type == "blockRef" || type == "image") && node.TryGetProperty("content", out _))
        {
            error = $"Node {path} (\"{type}\") may not have child content.";
            return false;
        }

        if (!TryValidateAttrs(node, type, path, out error))
        {
            return false;
        }

        if (node.TryGetProperty("content", out var contentProp))
        {
            if (contentProp.ValueKind != JsonValueKind.Array)
            {
                error = $"Node {path} property \"content\" must be an array.";
                return false;
            }

            var index = 0;
            foreach (var child in contentProp.EnumerateArray())
            {
                if (!TryValidateNode(child, $"{path}.{index}", depth + 1, allowBlockRefs, ref budget, out error))
                {
                    return false;
                }

                index++;
            }
        }

        return true;
    }

    private static bool TryValidateMarks(JsonElement node, string path, out string error)
    {
        error = string.Empty;

        if (!node.TryGetProperty("marks", out var marks))
        {
            return true;
        }

        if (marks.ValueKind != JsonValueKind.Array)
        {
            error = $"Node {path} property \"marks\" must be an array.";
            return false;
        }

        foreach (var mark in marks.EnumerateArray())
        {
            if (mark.ValueKind != JsonValueKind.Object
                || !mark.TryGetProperty("type", out var markType)
                || markType.ValueKind != JsonValueKind.String)
            {
                error = $"Node {path} has a mark without a string \"type\".";
                return false;
            }

            if (!AllowedMarkTypes.Contains(markType.GetString()!))
            {
                error = $"Node {path} uses unsupported mark \"{markType.GetString()}\".";
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Validates a node's <c>attrs</c>. Only <c>heading</c> and <c>mergeField</c>
    /// have meaningful attributes here; every other node must be attribute-free,
    /// so a payload can't smuggle unbounded data through an attrs bag.
    /// </summary>
    private static bool TryValidateAttrs(JsonElement node, string type, string path, out string error)
    {
        error = string.Empty;

        if (!node.TryGetProperty("attrs", out var attrs))
        {
            // mergeField without attrs has no binding — reject it below.
            if (type == "mergeField")
            {
                error = $"Node {path} (\"mergeField\") must have a \"name\" attribute.";
                return false;
            }

            // blockRef without attrs has no target.
            if (type == "blockRef")
            {
                error = $"Node {path} (\"blockRef\") must have an \"id\" attribute.";
                return false;
            }

            // image without attrs has no source.
            if (type == "image")
            {
                error = $"Node {path} (\"image\") must have an \"assetId\" attribute.";
                return false;
            }

            return true;
        }

        if (attrs.ValueKind != JsonValueKind.Object)
        {
            error = $"Node {path} property \"attrs\" must be an object.";
            return false;
        }

        switch (type)
        {
            case "heading":
                foreach (var attr in attrs.EnumerateObject())
                {
                    if (attr.Name != "level")
                    {
                        error = $"Node {path} (\"heading\") has unsupported attribute \"{attr.Name}\".";
                        return false;
                    }

                    if (attr.Value.ValueKind != JsonValueKind.Number
                        || !attr.Value.TryGetInt32(out var level)
                        || level < MinHeadingLevel
                        || level > MaxHeadingLevel)
                    {
                        error = $"Node {path} (\"heading\") \"level\" must be an integer "
                            + $"between {MinHeadingLevel} and {MaxHeadingLevel}.";
                        return false;
                    }
                }

                return true;

            case "mergeField":
                return TryValidateMergeFieldAttrs(attrs, path, out error);

            case "blockRef":
                return TryValidateBlockRefAttrs(attrs, path, out error);

            case "image":
                return TryValidateImageAttrs(attrs, path, out error);

            default:
                // Reject any unexpected attribute keys; ignore an empty attrs bag,
                // which TipTap sometimes emits.
                foreach (var attr in attrs.EnumerateObject())
                {
                    error = $"Node {path} (\"{type}\") does not accept attribute \"{attr.Name}\".";
                    return false;
                }

                return true;
        }
    }

    private static bool TryValidateMergeFieldAttrs(JsonElement attrs, string path, out string error)
    {
        error = string.Empty;

        var hasName = false;

        foreach (var attr in attrs.EnumerateObject())
        {
            if (attr.Name is not ("name" or "label" or "step"))
            {
                error = $"Node {path} (\"mergeField\") has unsupported attribute \"{attr.Name}\".";
                return false;
            }

            // step may be null (single-step forms); name and label are strings.
            if (attr.Value.ValueKind == JsonValueKind.Null)
            {
                if (attr.Name == "name")
                {
                    error = $"Node {path} (\"mergeField\") \"name\" may not be null.";
                    return false;
                }

                continue;
            }

            if (attr.Value.ValueKind != JsonValueKind.String)
            {
                error = $"Node {path} (\"mergeField\") attribute \"{attr.Name}\" must be a string.";
                return false;
            }

            if (attr.Value.GetString()!.Length > MaxAttrStringLength)
            {
                error = $"Node {path} (\"mergeField\") attribute \"{attr.Name}\" "
                    + $"exceeds {MaxAttrStringLength} characters.";
                return false;
            }

            if (attr.Name == "name" && !string.IsNullOrWhiteSpace(attr.Value.GetString()))
            {
                hasName = true;
            }
        }

        if (!hasName)
        {
            error = $"Node {path} (\"mergeField\") must have a non-empty \"name\".";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Validates a <c>blockRef</c> node's attrs: a required non-empty <c>id</c>
    /// (the block's Guid, kept as a bounded opaque string — the renderer treats an
    /// id it can't resolve as a missing block, so no GUID parsing is needed here)
    /// and an optional <c>name</c> cached for display.
    /// </summary>
    private static bool TryValidateBlockRefAttrs(JsonElement attrs, string path, out string error)
    {
        error = string.Empty;

        var hasId = false;

        foreach (var attr in attrs.EnumerateObject())
        {
            if (attr.Name is not ("id" or "name"))
            {
                error = $"Node {path} (\"blockRef\") has unsupported attribute \"{attr.Name}\".";
                return false;
            }

            // name may be null (display-only); id is a required string.
            if (attr.Value.ValueKind == JsonValueKind.Null)
            {
                if (attr.Name == "id")
                {
                    error = $"Node {path} (\"blockRef\") \"id\" may not be null.";
                    return false;
                }

                continue;
            }

            if (attr.Value.ValueKind != JsonValueKind.String)
            {
                error = $"Node {path} (\"blockRef\") attribute \"{attr.Name}\" must be a string.";
                return false;
            }

            if (attr.Value.GetString()!.Length > MaxAttrStringLength)
            {
                error = $"Node {path} (\"blockRef\") attribute \"{attr.Name}\" "
                    + $"exceeds {MaxAttrStringLength} characters.";
                return false;
            }

            if (attr.Name == "id" && !string.IsNullOrWhiteSpace(attr.Value.GetString()))
            {
                hasId = true;
            }
        }

        if (!hasId)
        {
            error = $"Node {path} (\"blockRef\") must have a non-empty \"id\".";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Validates an <c>image</c> node's attrs: a required non-empty <c>assetId</c>
    /// (the asset's Guid as a bounded opaque string — the renderer builds the src
    /// from it, so an unresolvable id just renders nothing) and an optional
    /// <c>alt</c>. Crucially there is no <c>src</c> attribute: the source is always
    /// derived from the id, never taken from stored content, so a template can
    /// never smuggle in an arbitrary or off-site image URL.
    /// </summary>
    private static bool TryValidateImageAttrs(JsonElement attrs, string path, out string error)
    {
        error = string.Empty;

        var hasAssetId = false;

        foreach (var attr in attrs.EnumerateObject())
        {
            if (attr.Name is not ("assetId" or "alt"))
            {
                error = $"Node {path} (\"image\") has unsupported attribute \"{attr.Name}\".";
                return false;
            }

            // alt may be null (decorative); assetId is a required string.
            if (attr.Value.ValueKind == JsonValueKind.Null)
            {
                if (attr.Name == "assetId")
                {
                    error = $"Node {path} (\"image\") \"assetId\" may not be null.";
                    return false;
                }

                continue;
            }

            if (attr.Value.ValueKind != JsonValueKind.String)
            {
                error = $"Node {path} (\"image\") attribute \"{attr.Name}\" must be a string.";
                return false;
            }

            if (attr.Value.GetString()!.Length > MaxAttrStringLength)
            {
                error = $"Node {path} (\"image\") attribute \"{attr.Name}\" "
                    + $"exceeds {MaxAttrStringLength} characters.";
                return false;
            }

            if (attr.Name == "assetId" && !string.IsNullOrWhiteSpace(attr.Value.GetString()))
            {
                hasAssetId = true;
            }
        }

        if (!hasAssetId)
        {
            error = $"Node {path} (\"image\") must have a non-empty \"assetId\".";
            return false;
        }

        return true;
    }
}
