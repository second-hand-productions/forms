using System.Text.Json;

namespace forms.Validation;

/// <summary>
/// Validates a FormKit schema before it is persisted.
///
/// This is a security boundary, not a convenience check. FormKit schemas are
/// executable: <c>$el</c>/<c>$cmp</c> nodes render arbitrary elements and
/// components, and any string beginning with <c>$</c> is evaluated as an
/// expression against the form context. Schemas here arrive from untrusted
/// sources — the builder UI and, later, LLM output — and are rendered in other
/// users' browsers, so we accept only a known-good subset.
///
/// Deny-by-default: unknown node types and unknown props are rejected rather
/// than passed through.
///
/// Container types (multi-step, step) may nest via <c>children</c>. Recursion is
/// bounded on three axes — depth, total node count across the whole tree, and
/// per-node prop values — so a deeply or widely nested payload cannot be used to
/// exhaust server or client resources.
/// </summary>
public static class FormSchemaValidator
{
    private const int MaxNodes = 200;
    private const int MaxStringLength = 2_000;

    /// <summary>multi-step (1) > step (2) > field (3). Nothing legitimate goes deeper.</summary>
    private const int MaxDepth = 3;

    private static readonly HashSet<string> AllowedInputTypes = new(StringComparer.Ordinal)
    {
        "text", "email", "number", "password", "tel", "url", "search",
        "textarea", "select", "radio", "checkbox",
        "date", "time", "datetime-local", "month", "week",
        "color", "range", "hidden",
    };

    /// <summary>Types permitted to carry <c>children</c>.</summary>
    private static readonly HashSet<string> AllowedContainerTypes = new(StringComparer.Ordinal)
    {
        "multi-step", "step",
    };

    private static readonly HashSet<string> AllowedProps = new(StringComparer.Ordinal)
    {
        "$formkit", "name", "label", "help", "placeholder", "validation",
        "validationLabel", "options", "value", "rows", "cols",
        "min", "max", "step", "multiple", "disabled", "id",
    };

    /// <summary>Props valid only on container nodes.</summary>
    private static readonly HashSet<string> AllowedContainerProps = new(StringComparer.Ordinal)
    {
        "$formkit", "name", "label", "children", "tabStyle", "allowIncomplete",
    };

    public static bool TryValidate(JsonElement schema, out string error)
    {
        error = string.Empty;

        if (schema.ValueKind != JsonValueKind.Array)
        {
            error = "Schema must be an array of nodes.";
            return false;
        }

        if (schema.GetArrayLength() == 0)
        {
            error = "Schema must contain at least one node.";
            return false;
        }

        // Budget is shared across the entire tree, not per level, so nesting
        // cannot multiply the effective node allowance.
        var budget = MaxNodes;
        return TryValidateNodeList(schema, depth: 1, path: string.Empty, ref budget, out error);
    }

    private static bool TryValidateNodeList(
        JsonElement list,
        int depth,
        string path,
        ref int budget,
        out string error)
    {
        error = string.Empty;

        if (depth > MaxDepth)
        {
            error = $"Schema nesting exceeds the maximum depth of {MaxDepth}.";
            return false;
        }

        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;

        foreach (var node in list.EnumerateArray())
        {
            if (--budget < 0)
            {
                error = $"Schema exceeds the maximum of {MaxNodes} nodes.";
                return false;
            }

            var nodePath = string.IsNullOrEmpty(path) ? $"{index}" : $"{path}.{index}";

            if (!TryValidateNode(node, nodePath, depth, seenNames, ref budget, out error))
            {
                return false;
            }

            index++;
        }

        return true;
    }

    private static bool TryValidateNode(
        JsonElement node,
        string path,
        int depth,
        HashSet<string> seenNames,
        ref int budget,
        out string error)
    {
        error = string.Empty;

        if (node.ValueKind != JsonValueKind.Object)
        {
            error = $"Node {path} must be an object.";
            return false;
        }

        if (!node.TryGetProperty("$formkit", out var typeProp)
            || typeProp.ValueKind != JsonValueKind.String)
        {
            error = $"Node {path} must have a string \"$formkit\" property.";
            return false;
        }

        var type = typeProp.GetString()!;
        var isContainer = AllowedContainerTypes.Contains(type);

        if (!isContainer && !AllowedInputTypes.Contains(type))
        {
            error = $"Node {path} uses unsupported input type \"{type}\".";
            return false;
        }

        if (!node.TryGetProperty("name", out var nameProp)
            || nameProp.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(nameProp.GetString()))
        {
            error = $"Node {path} must have a non-empty string \"name\".";
            return false;
        }

        // Uniqueness is scoped to siblings: each step is its own group in the
        // submitted payload, so the same field name in two steps is fine.
        if (!seenNames.Add(nameProp.GetString()!))
        {
            error = $"Node {path} has duplicate name \"{nameProp.GetString()}\".";
            return false;
        }

        var allowedProps = isContainer ? AllowedContainerProps : AllowedProps;

        foreach (var prop in node.EnumerateObject())
        {
            if (!allowedProps.Contains(prop.Name))
            {
                error = isContainer
                    ? $"Node {path} (\"{type}\") has unsupported property \"{prop.Name}\"."
                    : $"Node {path} has unsupported property \"{prop.Name}\".";
                return false;
            }

            if (prop.Name == "children")
            {
                if (prop.Value.ValueKind != JsonValueKind.Array)
                {
                    error = $"Node {path} property \"children\" must be an array.";
                    return false;
                }

                if (prop.Value.GetArrayLength() == 0)
                {
                    error = $"Node {path} property \"children\" must not be empty.";
                    return false;
                }

                if (!TryValidateNodeList(prop.Value, depth + 1, path, ref budget, out error))
                {
                    return false;
                }

                continue;
            }

            if (!TryValidateValue(prop.Value, path, prop.Name, out error))
            {
                return false;
            }
        }

        // A container without children renders nothing and usually signals a
        // client bug; reject rather than store a dead node.
        if (isContainer && !node.TryGetProperty("children", out _))
        {
            error = $"Node {path} (\"{type}\") must have children.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Rejects values that FormKit would evaluate rather than render literally,
    /// and caps sizes. Only scalars and a flat options map are permitted.
    /// </summary>
    private static bool TryValidateValue(
        JsonElement value,
        string path,
        string propName,
        out string error)
    {
        error = string.Empty;

        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                var text = value.GetString()!;
                if (text.Length > MaxStringLength)
                {
                    error = $"Node {path} property \"{propName}\" exceeds {MaxStringLength} characters.";
                    return false;
                }

                // A leading "$" makes FormKit treat the string as an expression.
                if (text.StartsWith('$'))
                {
                    error = $"Node {path} property \"{propName}\" may not start with \"$\".";
                    return false;
                }

                return true;

            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
                return true;

            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                {
                    if (item.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
                    {
                        error = $"Node {path} property \"{propName}\" may not nest arrays or objects.";
                        return false;
                    }

                    if (!TryValidateValue(item, path, propName, out error))
                    {
                        return false;
                    }
                }

                return true;

            case JsonValueKind.Object:
                // Only the flat { value: label } options map is allowed.
                if (propName != "options")
                {
                    error = $"Node {path} property \"{propName}\" may not be an object.";
                    return false;
                }

                foreach (var entry in value.EnumerateObject())
                {
                    if (entry.Value.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
                    {
                        error = $"Node {path} option \"{entry.Name}\" must be a scalar.";
                        return false;
                    }

                    if (!TryValidateValue(entry.Value, path, propName, out error))
                    {
                        return false;
                    }
                }

                return true;

            default:
                error = $"Node {path} property \"{propName}\" has an unsupported value.";
                return false;
        }
    }
}
