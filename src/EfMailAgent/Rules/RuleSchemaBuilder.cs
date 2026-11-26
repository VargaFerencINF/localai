using System.Text.Json.Nodes;

namespace EfMailAgent.Rules;

/// <summary>
/// Builds the JSON Schema the LLM output must follow (CLAUDE.md section 5). Shape is compatible with OpenAI strict
/// structured outputs and Ollama's "format": every property is listed in "required", optional columns are nullable,
/// and additionalProperties is false everywhere.
/// </summary>
public static class RuleSchemaBuilder
{
    public static JsonObject Build(Rule rule) => Build(rule.Output);

    public static JsonObject Build(RuleOutput output)
    {
        var properties = new JsonObject();
        foreach (var column in output.Columns)
            properties[column.Name] = BuildColumn(column);

        var item = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JsonArray(output.Columns.Select(c => (JsonNode?)JsonValue.Create(c.Name)).ToArray()),
            ["additionalProperties"] = false,
        };

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                [output.RowsPath] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = item,
                },
            },
            ["required"] = new JsonArray(JsonValue.Create(output.RowsPath)),
            ["additionalProperties"] = false,
        };
    }

    private static JsonObject BuildColumn(RuleColumn column)
    {
        var jsonType = column.Type switch
        {
            ColumnType.Number => "number",
            ColumnType.Integer => "integer",
            ColumnType.Boolean => "boolean",
            _ => "string",
        };

        var schema = new JsonObject
        {
            ["type"] = column.Required ? jsonType : new JsonArray(jsonType, "null"),
        };

        if (column.Type == ColumnType.Date)
            schema["format"] = "date";

        if (column.Type == ColumnType.Enum)
        {
            var values = new JsonArray(column.Values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
            if (!column.Required)
                values.Add(null);
            schema["enum"] = values;
        }

        if (column.Description.Length > 0)
            schema["description"] = column.Description;

        return schema;
    }
}
