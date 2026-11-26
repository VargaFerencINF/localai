using System.Text.Json.Nodes;
using EfMailAgent.Addon;

namespace EfMailAgent.Tests.Addon;

public class PolicyFileTests
{
    private static readonly AddonPolicy Policy = new(AddonPackage.ExpectedId, "file:///C:/ProgramData/EfMailAgent/addon/efmailagent-0.2.0.xpi", "http://127.0.0.1:47811", "uj-token");

    [Fact]
    public void Merge_into_nothing_creates_the_documented_structure()
    {
        var merged = PolicyFile.Merge(null, Policy);

        var expected = JsonNode.Parse("""
            {
              "policies": {
                "ExtensionSettings": {
                  "efmailagent@energofish.hu": { "installation_mode": "force_installed", "install_url": "file:///C:/ProgramData/EfMailAgent/addon/efmailagent-0.2.0.xpi" }
                },
                "3rdparty": {
                  "Extensions": {
                    "efmailagent@energofish.hu": { "serverUrl": "http://127.0.0.1:47811", "token": "uj-token" }
                  }
                }
              }
            }
            """);
        Assert.True(JsonNode.DeepEquals(expected, merged), merged.ToJsonString());
    }

    [Fact]
    public void Merge_keeps_every_unrelated_key_and_replaces_an_older_entry_of_ours()
    {
        var existing = JsonNode.Parse("""
            {
              "comment": "kézzel írt",
              "policies": {
                "DisableAppUpdate": true,
                "Certificates": { "ImportEnterpriseRoots": true },
                "ExtensionSettings": {
                  "*": { "installation_mode": "blocked" },
                  "efmailagent@energofish.hu": { "installation_mode": "normal_installed", "install_url": "file:///regi.xpi", "updates_disabled": true }
                },
                "3rdparty": {
                  "Extensions": {
                    "uBlock0@raymondhill.net": { "adminSettings": { "x": 1 } },
                    "efmailagent@energofish.hu": { "serverUrl": "http://127.0.0.1:1", "token": "regi" }
                  }
                }
              }
            }
            """)!.AsObject();
        var before = existing.ToJsonString();

        var merged = PolicyFile.Merge(existing, Policy);

        Assert.Equal(before, existing.ToJsonString()); // input untouched
        Assert.Equal("kézzel írt", merged["comment"]!.GetValue<string>());
        Assert.True(merged["policies"]!["DisableAppUpdate"]!.GetValue<bool>());
        Assert.True(merged["policies"]!["Certificates"]!["ImportEnterpriseRoots"]!.GetValue<bool>());
        Assert.Equal("blocked", merged["policies"]!["ExtensionSettings"]!["*"]!["installation_mode"]!.GetValue<string>());
        Assert.Equal(1, merged["policies"]!["3rdparty"]!["Extensions"]!["uBlock0@raymondhill.net"]!["adminSettings"]!["x"]!.GetValue<int>());
        var ours = merged["policies"]!["ExtensionSettings"]![AddonPackage.ExpectedId]!.AsObject();
        Assert.Equal(["installation_mode", "install_url"], ours.Select(p => p.Key));
        Assert.Equal("force_installed", ours["installation_mode"]!.GetValue<string>());
        Assert.Equal(Policy, PolicyFile.Read(merged, AddonPackage.ExpectedId));
    }

    [Fact]
    public void Remove_takes_out_only_our_entries_and_empty_containers()
    {
        var withOthers = PolicyFile.Merge(JsonNode.Parse("""
            { "policies": { "DisableAppUpdate": true, "ExtensionSettings": { "other@x": { "installation_mode": "allowed" } },
                            "3rdparty": { "Extensions": { "other@x": { "a": 1 } } } } }
            """)!.AsObject(), Policy);

        var removed = PolicyFile.Remove(withOthers, AddonPackage.ExpectedId);

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""
            { "policies": { "DisableAppUpdate": true, "ExtensionSettings": { "other@x": { "installation_mode": "allowed" } },
                            "3rdparty": { "Extensions": { "other@x": { "a": 1 } } } } }
            """), removed), removed.ToJsonString());

        var onlyOurs = PolicyFile.Remove(PolicyFile.Merge(JsonNode.Parse("""{ "policies": { "DisableTelemetry": true } }""")!.AsObject(), Policy), AddonPackage.ExpectedId);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{ "policies": { "DisableTelemetry": true } }"""), onlyOurs), onlyOurs.ToJsonString());
    }

    [Theory]
    [InlineData(null, PolicyFileState.Missing)]
    [InlineData("", PolicyFileState.Empty)]
    [InlineData("   \r\n", PolicyFileState.Empty)]
    [InlineData("{ \"policies\": { } }", PolicyFileState.Valid)]
    [InlineData("{ // megjegyzés\n \"policies\": { }, }", PolicyFileState.Valid)]
    [InlineData("{ broken", PolicyFileState.Malformed)]
    [InlineData("[1, 2]", PolicyFileState.Malformed)]
    [InlineData("{ \"policies\": [] }", PolicyFileState.Malformed)]
    public void Load_classifies_files(string? content, PolicyFileState expected)
    {
        using var tmp = new TempDir();
        var path = tmp.Combine("policies.json");
        if (content is not null)
            File.WriteAllText(path, content);

        Assert.Equal(expected, PolicyFile.Load(path).State);
    }

    [Fact]
    public void Write_is_pretty_utf8_without_bom_and_creates_the_folder()
    {
        using var tmp = new TempDir();
        var path = tmp.Combine("distribution", "policies.json");

        PolicyFile.Write(path, PolicyFile.Merge(JsonNode.Parse("""{"policies":{"Megjegyzés":"ő"}}""")!.AsObject(), Policy));

        var bytes = File.ReadAllBytes(path);
        Assert.NotEqual(0xEF, bytes[0]);
        var text = File.ReadAllText(path);
        Assert.Contains("\n  \"policies\": {", text.Replace("\r\n", "\n"));
        Assert.Contains("\"Megjegyzés\": \"ő\"", text);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Backups_never_overwrite_each_other()
    {
        using var tmp = new TempDir();
        var path = tmp.WriteFile("policies.json", "{}");
        var now = new DateTimeOffset(2026, 11, 26, 10, 0, 0, TimeSpan.Zero);

        var first = PolicyFile.Backup(path, now);
        var second = PolicyFile.Backup(path, now);

        Assert.NotEqual(first, second);
        Assert.True(File.Exists(first) && File.Exists(second));
        Assert.Null(PolicyFile.Backup(tmp.Combine("nincs.json"), now));
    }
}
