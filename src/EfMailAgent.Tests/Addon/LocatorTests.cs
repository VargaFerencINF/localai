using EfMailAgent.Addon;
using static EfMailAgent.Tests.Addon.AddonTestSupport;

namespace EfMailAgent.Tests.Addon;

public class LocatorTests
{
    [Fact]
    public void Registry_views_and_default_paths_are_combined_without_duplicates()
    {
        using var tmp = new TempDir();
        var x64 = CreateThunderbird(tmp, "TB64", "153.2.0esr");
        var x86 = CreateThunderbird(tmp, "TB32", "140.5.0esr");
        var registry = new FakeRegistry();
        registry.AddInstall("153.2.0esr (x64 hu)", x64, wow64: false);
        registry.AddInstall("140.5.0esr (x86 hu)", "\"" + x86 + "\"", wow64: true);
        registry.AddInstall("115.0 (x64 hu)", tmp.Combine("torolve"), wow64: false); // stale registry entry

        var found = new ThunderbirdLocator(registry, [x64, tmp.Combine("nincs")]).FindInstallations();

        Assert.Equal([Path.GetFullPath(x64), Path.GetFullPath(x86)], found.Select(f => f.InstallDir));
        Assert.Equal(["153.2.0esr", "140.5.0esr"], found.Select(f => f.Version));
        Assert.Contains("64", found[0].Source);
        Assert.Contains("32", found[1].Source);
    }

    [Fact]
    public void Default_paths_are_used_without_registry()
    {
        using var tmp = new TempDir();
        var tb = CreateThunderbird(tmp);
        var found = new ThunderbirdLocator(null, [tb]).FindInstallations();
        Assert.Equal("default path", Assert.Single(found).Source);
    }

    [Fact]
    public void Combined_locator_adds_known_folders_first()
    {
        using var tmp = new TempDir();
        var known = CreateThunderbird(tmp, "Ismert");
        var other = CreateThunderbird(tmp, "Masik");
        var found = new CombinedThunderbirdLocator([known, tmp.Combine("nincs")], new ThunderbirdLocator(null, [other, known])).FindInstallations();
        Assert.Equal([Path.GetFullPath(known), Path.GetFullPath(other)], found.Select(f => f.InstallDir));
    }

    [Fact]
    public void Profiles_ini_relative_absolute_and_default_flag()
    {
        using var tmp = new TempDir();
        var absolute = tmp.Combine("valahol", "abs.profile");
        tmp.WriteFile("Thunderbird/profiles.ini", $"""
            [General]
            StartWithLastProfile=1

            [Profile0]
            Name=default
            IsRelative=1
            Path=Profiles/abc.default
            Default=1

            [Profile1]
            Name=kulso
            IsRelative=0
            Path={absolute}
            """);

        var profiles = ThunderbirdProfiles.Read(tmp.Combine("Thunderbird"));

        Assert.Equal(2, profiles.Count);
        Assert.Equal(Path.GetFullPath(tmp.Combine("Thunderbird", "Profiles", "abc.default")), profiles[0].Path);
        Assert.True(profiles[0].IsDefault);
        Assert.Equal(absolute, profiles[1].Path);
        Assert.False(profiles[1].IsDefault);
        Assert.Empty(ThunderbirdProfiles.Read(tmp.Combine("nincs")));
    }

    [Fact]
    public void Xpi_manifest_is_read_and_versioned_file_url_is_built()
    {
        using var tmp = new TempDir();
        var package = AddonPackage.Read(CreateXpi(tmp.Combine("efmailagent.xpi"), "1.2.3"));
        Assert.Equal(AddonPackage.ExpectedId, package.Id);
        Assert.Equal("1.2.3", package.Version);
        Assert.Equal("efmailagent-1.2.3.xpi", package.InstalledFileName);
        Assert.StartsWith("file:///", AddonInstaller.FileUrl(tmp.Combine("a b", "x.xpi")));
        Assert.Contains("a%20b", AddonInstaller.FileUrl(tmp.Combine("a b", "x.xpi")));
        Assert.Throws<AddonException>(() => AddonPackage.Read(tmp.WriteFile("rossz.xpi", "nem zip")));
    }
}
