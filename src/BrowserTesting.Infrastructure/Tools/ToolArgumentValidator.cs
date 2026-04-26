using System.Text.Json.Nodes;
using BrowserTesting.Core.Llm;

namespace BrowserTesting.Infrastructure.Tools;

public static class ToolArgumentValidator
{
    public static ToolArgumentValidationResult? Validate(LlmToolDefinition definition, JsonObject arguments)
    {
        var schema = definition.Parameters;
        if (!string.Equals(schema["type"]?.GetValue<string>(), "object", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var properties = schema["properties"]?.AsObject();
        var required = schema["required"]?.AsArray();
        if (properties is null)
        {
            return null;
        }

        var issues = new List<string>();
        ValidateObject(arguments, properties, required, definition.Name, string.Empty, issues);
        if (issues.Count == 0)
        {
            return null;
        }

        var hint = BuildHint(definition.Name, arguments, properties);
        return new ToolArgumentValidationResult(
            $"Tool `{definition.Name}` received invalid arguments.",
            string.Join(" ", issues),
            schema.DeepClone(),
            BuildExampleArguments(properties, required),
            hint);
    }

    private static void ValidateObject(
        JsonObject value,
        JsonObject properties,
        JsonArray? required,
        string toolName,
        string path,
        List<string> issues)
    {
        if (required is not null)
        {
            foreach (var item in required)
            {
                var propertyName = item?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(propertyName))
                {
                    continue;
                }

                if (value[propertyName] is null)
                {
                    issues.Add($"Missing required argument `{BuildPath(path, propertyName)}`.");
                }
            }
        }

        foreach (var property in properties)
        {
            if (property.Value is not JsonObject propertySchema)
            {
                continue;
            }

            var propertyValue = value[property.Key];
            if (propertyValue is null)
            {
                continue;
            }

            ValidateNode(propertyValue, propertySchema, toolName, BuildPath(path, property.Key), issues);
        }
    }

    private static void ValidateNode(
        JsonNode? value,
        JsonObject schema,
        string toolName,
        string path,
        List<string> issues)
    {
        if (schema["anyOf"] is JsonArray anyOfSchemas && anyOfSchemas.Count > 0)
        {
            foreach (var candidate in anyOfSchemas.OfType<JsonObject>())
            {
                var candidateIssues = new List<string>();
                ValidateNode(value, candidate, toolName, path, candidateIssues);
                if (candidateIssues.Count == 0)
                {
                    return;
                }
            }

            issues.Add($"Argument `{path}` does not match any allowed type.");
            return;
        }

        var type = schema["type"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(type))
        {
            return;
        }

        if (schema["enum"] is JsonArray choices && choices.Count > 0 && value is JsonValue enumValue)
        {
            var allowed = choices
                .Select(choice => choice?.GetValue<string>())
                .Where(choice => !string.IsNullOrWhiteSpace(choice))
                .ToArray();
            if (enumValue.TryGetValue<string>(out var text) &&
                allowed.Any(choice => string.Equals(choice, text, StringComparison.Ordinal)))
            {
                return;
            }

            issues.Add($"Argument `{path}` must be one of: {string.Join(", ", allowed)}.");
            return;
        }

        if (value is null)
        {
            if (string.Equals(type, "null", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            issues.Add($"Argument `{path}` must not be null.");
            return;
        }

        switch (type)
        {
            case "object":
                if (value is not JsonObject objectValue)
                {
                    issues.Add($"Argument `{path}` must be an object.");
                    return;
                }

                ValidateObject(
                    objectValue,
                    schema["properties"]?.AsObject() ?? new JsonObject(),
                    schema["required"]?.AsArray(),
                    toolName,
                    path,
                    issues);
                break;

            case "array":
                if (value is not JsonArray arrayValue)
                {
                    issues.Add($"Argument `{path}` must be an array.");
                    return;
                }

                if (schema["items"] is JsonObject itemSchema)
                {
                    for (var index = 0; index < arrayValue.Count; index++)
                    {
                        ValidateNode(arrayValue[index], itemSchema, toolName, $"{path}[{index}]", issues);
                    }
                }

                break;

            case "string":
                if (!IsString(value))
                {
                    issues.Add($"Argument `{path}` must be a string.");
                }

                break;

            case "integer":
                if (!IsInteger(value))
                {
                    issues.Add($"Argument `{path}` must be an integer.");
                }

                break;

            case "number":
                if (!IsNumber(value))
                {
                    issues.Add($"Argument `{path}` must be a number.");
                }

                break;

            case "boolean":
                if (!IsBoolean(value))
                {
                    issues.Add($"Argument `{path}` must be a boolean.");
                }

                break;

            case "null":
                if (value is not null)
                {
                    issues.Add($"Argument `{path}` must be null.");
                }

                break;
        }
    }

    private static ToolArgumentHint? BuildHint(string toolName, JsonObject arguments, JsonObject properties)
    {
        if (properties.ContainsKey("locator") &&
            arguments["locator"] is null &&
            IsString(arguments["strategy"]) &&
            IsString(arguments["value"]))
        {
            var strategy = arguments["strategy"]!.GetValue<string>();
            var value = arguments["value"]!.GetValue<string>();

            return new ToolArgumentHint(
                "Wrap `strategy` and `value` inside a top-level `locator` object.",
                new JsonObject
                {
                    ["locator"] = new JsonObject
                    {
                        ["strategy"] = strategy,
                        ["value"] = value,
                    },
                });
        }

        return null;
    }

    private static JsonObject BuildExampleArguments(JsonObject properties, JsonArray? required)
    {
        var example = new JsonObject();
        var requiredNames = required?
            .Select(item => item?.GetValue<string>())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.Ordinal)
            ?? [];

        foreach (var property in properties)
        {
            if (property.Value is not JsonObject propertySchema)
            {
                continue;
            }

            if (requiredNames.Count > 0 && !requiredNames.Contains(property.Key))
            {
                continue;
            }

            example[property.Key] = BuildExampleValue(property.Key, propertySchema);
        }

        return example;
    }

    private static JsonNode? BuildExampleValue(string propertyName, JsonObject schema)
    {
        if (schema["anyOf"] is JsonArray anyOfSchemas)
        {
            foreach (var candidate in anyOfSchemas.OfType<JsonObject>())
            {
                if (string.Equals(candidate["type"]?.GetValue<string>(), "null", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var example = BuildExampleValue(propertyName, candidate);
                if (example is not null)
                {
                    return example;
                }
            }
        }

        if (schema["enum"] is JsonArray choices && choices.Count > 0)
        {
            return choices[0]?.DeepClone();
        }

        return schema["type"]?.GetValue<string>() switch
        {
            "object" => BuildExampleArguments(schema["properties"]?.AsObject() ?? new JsonObject(), schema["required"]?.AsArray()),
            "array" => new JsonArray(),
            "integer" => JsonValue.Create(5000),
            "boolean" => JsonValue.Create(true),
            "string" => JsonValue.Create(GetExampleString(propertyName)),
            _ => null,
        };
    }

    private static string GetExampleString(string propertyName) =>
        propertyName switch
        {
            "url" => "https://www.google.com",
            "goal_id" => "<goal-id>",
            "success_criteria" => "Observed expected page evidence",
            "title" => "Verify page behavior",
            "text" => "Example text",
            "value" => "selector-or-value",
            "strategy" => "css",
            "reason" => "Observed behavior did not meet the goal",
            "evidence" => "Captured page evidence",
            "attribute" => "aria-label",
            _ => $"<{propertyName}>",
        };

    private static string BuildPath(string path, string propertyName) =>
        string.IsNullOrWhiteSpace(path) ? propertyName : $"{path}.{propertyName}";

    private static bool IsString(JsonNode? value) =>
        value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out _);

    private static bool IsBoolean(JsonNode? value) =>
        value is JsonValue jsonValue && jsonValue.TryGetValue<bool>(out _);

    private static bool IsInteger(JsonNode? value) =>
        value is JsonValue jsonValue && (jsonValue.TryGetValue<int>(out _) || jsonValue.TryGetValue<long>(out _));

    private static bool IsNumber(JsonNode? value) =>
        value is JsonValue jsonValue &&
        (jsonValue.TryGetValue<double>(out _) ||
         jsonValue.TryGetValue<decimal>(out _) ||
         jsonValue.TryGetValue<int>(out _) ||
         jsonValue.TryGetValue<long>(out _));
}

public sealed record ToolArgumentValidationResult(
    string Summary,
    string Error,
    JsonNode? ExpectedArguments,
    JsonNode? ExampleArguments,
    ToolArgumentHint? Hint);

public sealed record ToolArgumentHint(
    string Message,
    JsonNode? NormalizedArguments);
