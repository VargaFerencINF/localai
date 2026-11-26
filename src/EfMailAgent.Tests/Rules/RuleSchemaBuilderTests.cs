using System.Text.Json.Nodes;
using EfMailAgent.Rules;

namespace EfMailAgent.Tests.Rules;

public class RuleSchemaBuilderTests
{
    [Fact]
    public void Sample_rule_schema_matches_snapshot()
    {
        var rule = RuleLoader.LoadAll(TestPaths.SampleRules).FindById("eloreendeles");
        Assert.NotNull(rule);

        var actual = RuleSchemaBuilder.Build(rule);
        var expected = JsonNode.Parse(File.ReadAllText(TestPaths.Fixture("eloreendeles.schema.json")));

        Assert.True(JsonNode.DeepEquals(expected, actual), "Schema differs from snapshot:\n" + actual.ToJsonString(new() { WriteIndented = true }));
    }

    [Fact]
    public void Optional_enum_allows_null_and_property_order_follows_columns()
    {
        var output = new RuleOutput(OutputMode.Append, "x.csv", "items",
        [
            new RuleColumn("b", ColumnType.Boolean, true, "", []),
            new RuleColumn("a", ColumnType.Enum, false, "", ["x", "y"]),
            new RuleColumn("i", ColumnType.Integer, false, "egész", []),
        ]);

        var schema = RuleSchemaBuilder.Build(output);
        var items = schema["properties"]!["items"]!["items"]!;

        Assert.Equal(["b", "a", "i"], items["properties"]!.AsObject().Select(p => p.Key));
        Assert.Equal("boolean", items["properties"]!["b"]!["type"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""["x","y",null]"""), items["properties"]!["a"]!["enum"]));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""["integer","null"]"""), items["properties"]!["i"]!["type"]));
        Assert.Null(items["properties"]!["b"]!["description"]);
        Assert.Equal("items", schema["required"]![0]!.GetValue<string>());
    }
}
