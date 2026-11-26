using System.IO.Compression;
using EfMailAgent.Addon;
using EfMailAgent.Configuration;

namespace EfMailAgent.Tests.Addon;

internal static class AddonTestSupport
{
    public static string CreateXpi(string path, string version = "0.1.0", string id = AddonPackage.ExpectedId, bool withManifest = true)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
            File.Delete(path);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        if (withManifest)
        {
            using var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open());
            writer.Write($$$"""{"manifest_version": 3, "version": "{{{version}}}", "browser_specific_settings": {"gecko": {"id": "{{{id}}}"} } }""");
        }
        using (var other = new StreamWriter(zip.CreateEntry("src/background.js").Open()))
            other.Write("// test");
        return path;
    }

    public static string CreateThunderbird(TempDir tmp, string name = "Mozilla Thunderbird", string version = "140.5.0esr")
    {
        var dir = tmp.Combine(name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "thunderbird.exe"), "");
        File.WriteAllText(Path.Combine(dir, "application.ini"), $"[App]\nVendor=Mozilla\nName=Thunderbird\nVersion={version}\nBuildID=1\n");
        return dir;
    }

    public static EffectiveSettings Settings(TempDir tmp, int port = 47811)
    {
        var app = new AppSettings();
        app.Server.Port = port;
        app.Server.Token = TokenGenerator.NewToken();
        return new EffectiveSettings(new DataDirectory(tmp.Combine("data"), DataDirectorySource.CommandLine), app);
    }

    public sealed class FakeElevation(bool elevated) : IElevationCheck
    {
        public bool IsElevated() => elevated;
    }

    public sealed class FakeRegistry : IRegistryReader
    {
        public Dictionary<(string Path, bool Wow64), List<string>> SubKeys { get; } = new();
        public Dictionary<(string Path, string Name, bool Wow64), string> Values { get; } = new();

        public void AddInstall(string version, string dir, bool wow64)
        {
            var key = (ThunderbirdLocator.RegistryPath, wow64);
            if (!SubKeys.TryGetValue(key, out var list))
                SubKeys[key] = list = [];
            list.Add(version);
            Values[($@"{ThunderbirdLocator.RegistryPath}\{version}\Main", "Install Directory", wow64)] = dir;
        }

        public IReadOnlyList<string> GetSubKeyNames(string path, bool wow64) => SubKeys.GetValueOrDefault((path, wow64)) ?? [];

        public string? GetValue(string path, string name, bool wow64) => Values.GetValueOrDefault((path, name, wow64));
    }
}
