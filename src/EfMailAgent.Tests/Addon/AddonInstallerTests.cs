using System.Text.Json.Nodes;
using EfMailAgent.Addon;
using static EfMailAgent.Tests.Addon.AddonTestSupport;

namespace EfMailAgent.Tests.Addon;

public class AddonInstallerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 10, 0, 0, TimeSpan.Zero);

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static AddonInstaller Installer(TempDir tmp, string thunderbirdDir, bool elevated = true, EfMailAgent.Configuration.EffectiveSettings? settings = null) =>
        new(settings ?? Settings(tmp), new FixedThunderbirdLocator(thunderbirdDir), new FakeElevation(elevated), new Clock());

    [Fact]
    public void Policy_install_into_folder_without_policies_json()
    {
        using var tmp = new TempDir();
        var tb = CreateThunderbird(tmp);
        var settings = Settings(tmp, port: 47900);
        var xpi = CreateXpi(tmp.Combine("dist", "efmailagent.xpi"), "0.3.0");

        var result = Installer(tmp, tb, settings: settings).Install(new InstallOptions(XpiPath: xpi));

        Assert.True(result.Success, string.Join("\n", result.Messages));
        var installedXpi = Path.Combine(settings.AddonDir, "efmailagent-0.3.0.xpi");
        Assert.True(File.Exists(installedXpi));
        var policy = PolicyFile.Read(PolicyFile.Load(PolicyFile.PolicyPath(tb)).Root, AddonPackage.ExpectedId)!;
        Assert.Equal(AddonInstaller.FileUrl(installedXpi), policy.InstallUrl);
        Assert.StartsWith("file:///", policy.InstallUrl);
        Assert.Equal("http://127.0.0.1:47900", policy.ServerUrl);
        Assert.Equal(settings.Settings.Server.Token, policy.Token);
        Assert.Empty(Directory.GetFiles(Path.Combine(tb, "distribution"), "*.bak-*")); // nothing to back up
        Assert.Contains(result.Messages, m => m.Contains("következő indításakor"));
        Assert.True(File.Exists(Path.Combine(settings.AddonDir, AddonInstaller.StateFileName)));
    }

    [Fact]
    public void Empty_policies_json_is_filled_and_backed_up()
    {
        using var tmp = new TempDir();
        var tb = CreateThunderbird(tmp);
        tmp.WriteFile("Mozilla Thunderbird/distribution/policies.json", "");

        var result = Installer(tmp, tb).Install(new InstallOptions(XpiPath: CreateXpi(tmp.Combine("x.xpi"))));

        Assert.True(result.Success, string.Join("\n", result.Messages));
        Assert.Equal(PolicyFileState.Valid, PolicyFile.Load(PolicyFile.PolicyPath(tb)).State);
        Assert.Single(Directory.GetFiles(Path.Combine(tb, "distribution"), "policies.json.bak-*"));
    }

    [Fact]
    public void Second_install_is_idempotent()
    {
        using var tmp = new TempDir();
        var tb = CreateThunderbird(tmp);
        var settings = Settings(tmp);
        var xpi = CreateXpi(tmp.Combine("x.xpi"));
        Installer(tmp, tb, settings: settings).Install(new InstallOptions(XpiPath: xpi));
        var before = File.ReadAllText(PolicyFile.PolicyPath(tb));

        var second = Installer(tmp, tb, settings: settings).Install(new InstallOptions(XpiPath: xpi));

        Assert.True(second.Success);
        Assert.Contains(second.Messages, m => m.Contains("már naprakész"));
        Assert.Equal(before, File.ReadAllText(PolicyFile.PolicyPath(tb)));
        Assert.Empty(Directory.GetFiles(Path.Combine(tb, "distribution"), "*.bak-*"));
    }

    [Fact]
    public void Upgrade_switches_to_the_new_versioned_xpi_and_removes_the_old_one()
    {
        using var tmp = new TempDir();
        var tb = CreateThunderbird(tmp);
        var settings = Settings(tmp);
        Installer(tmp, tb, settings: settings).Install(new InstallOptions(XpiPath: CreateXpi(tmp.Combine("a.xpi"), "0.1.0")));

        Installer(tmp, tb, settings: settings).Install(new InstallOptions(XpiPath: CreateXpi(tmp.Combine("b.xpi"), "0.2.0")));

        Assert.Equal(["efmailagent-0.2.0.xpi"], Directory.GetFiles(settings.AddonDir, "*.xpi").Select(Path.GetFileName));
        Assert.EndsWith("efmailagent-0.2.0.xpi", PolicyFile.Read(PolicyFile.Load(PolicyFile.PolicyPath(tb)).Root, AddonPackage.ExpectedId)!.InstallUrl);
    }

    [Fact]
    public void Malformed_policies_json_is_reported_and_never_overwritten()
    {
        using var tmp = new TempDir();
        var tb = CreateThunderbird(tmp);
        var path = tmp.WriteFile("Mozilla Thunderbird/distribution/policies.json", "{ \"policies\": { \"DisableAppUpdate\": true, ");

        var result = Installer(tmp, tb).Install(new InstallOptions(XpiPath: CreateXpi(tmp.Combine("x.xpi"))));

        Assert.Equal(AddonExitCodes.PolicyFileMalformed, result.ExitCode);
        Assert.Contains("hibás JSON", result.Messages.Single());
        Assert.Equal("{ \"policies\": { \"DisableAppUpdate\": true, ", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.bak-*"));
    }

    [Fact]
    public void Missing_admin_rights_give_a_hungarian_error_and_change_nothing()
    {
        using var tmp = new TempDir();
        var tb = CreateThunderbird(tmp);

        var result = Installer(tmp, tb, elevated: false).Install(new InstallOptions(XpiPath: CreateXpi(tmp.Combine("x.xpi"))));

        Assert.Equal(AddonExitCodes.NotElevated, result.ExitCode);
        Assert.Contains("rendszergazdai jog", result.Messages.Single());
        Assert.False(File.Exists(PolicyFile.PolicyPath(tb)));
    }

    [Fact]
    public void No_thunderbird_and_no_xpi_are_reported()
    {
        using var tmp = new TempDir();
        var noThunderbird = new AddonInstaller(Settings(tmp), new ThunderbirdLocator(null, [tmp.Combine("nincs")]), new FakeElevation(true));
        var result = noThunderbird.Install(new InstallOptions(XpiPath: CreateXpi(tmp.Combine("x.xpi"))));
        Assert.Equal(AddonExitCodes.AddonError, result.ExitCode);
        Assert.Contains("Thunderbird nem található", result.Messages.Single());

        var noXpi = Installer(tmp, CreateThunderbird(tmp)).Install(new InstallOptions(XpiPath: tmp.Combine("nincs.xpi")));
        Assert.Equal(AddonExitCodes.AddonError, noXpi.ExitCode);

        var wrongId = Installer(tmp, CreateThunderbird(tmp)).Install(new InstallOptions(XpiPath: CreateXpi(tmp.Combine("w.xpi"), id: "mas@x")));
        Assert.Contains("azonosítója 'mas@x'", wrongId.Messages.Single());

        var noManifest = Installer(tmp, CreateThunderbird(tmp)).Install(new InstallOptions(XpiPath: CreateXpi(tmp.Combine("m.xpi"), withManifest: false)));
        Assert.Contains("manifest.json", noManifest.Messages.Single());
    }

    [Fact]
    public void Uninstall_removes_only_our_keys_keeps_backups_and_profile_xpi_is_deleted()
    {
        using var tmp = new TempDir();
        var tb = CreateThunderbird(tmp);
        var settings = Settings(tmp);
        tmp.WriteFile("Mozilla Thunderbird/distribution/policies.json", """{ "policies": { "DisableAppUpdate": true } }""");
        Installer(tmp, tb, settings: settings).Install(new InstallOptions(XpiPath: CreateXpi(tmp.Combine("x.xpi"))));
        var profile = tmp.Combine("profiles", "abc.default");
        Directory.CreateDirectory(Path.Combine(profile, "extensions"));
        File.WriteAllText(ThunderbirdProfiles.ExtensionPath(profile), "xpi");

        var result = Installer(tmp, tb, settings: settings).Uninstall(new UninstallOptions(ProfilePath: profile));

        Assert.True(result.Success, string.Join("\n", result.Messages));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{ "policies": { "DisableAppUpdate": true } }"""), PolicyFile.Load(PolicyFile.PolicyPath(tb)).Root));
        Assert.Equal(2, Directory.GetFiles(Path.Combine(tb, "distribution"), "policies.json.bak-*").Length);
        Assert.False(File.Exists(ThunderbirdProfiles.ExtensionPath(profile)));
        Assert.False(File.Exists(Path.Combine(settings.AddonDir, AddonInstaller.StateFileName)));
    }

    [Fact]
    public void Profile_mode_copies_the_xpi_into_the_default_profile()
    {
        using var tmp = new TempDir();
        var root = tmp.Combine("Thunderbird");
        tmp.WriteFile("Thunderbird/profiles.ini", """
            [Profile1]
            Name=masik
            IsRelative=1
            Path=Profiles/m.masik

            [Profile0]
            Name=default-release
            IsRelative=1
            Path=Profiles/x.default-release

            [Install6F193CCC56814779]
            Default=Profiles/x.default-release
            Locked=1
            """);

        var result = Installer(tmp, CreateThunderbird(tmp)).Install(new InstallOptions(InstallMode.Profile, CreateXpi(tmp.Combine("x.xpi")), UserProfileRoot: root));

        Assert.True(result.Success, string.Join("\n", result.Messages));
        Assert.True(File.Exists(Path.Combine(root, "Profiles", "x.default-release", "extensions", "efmailagent@energofish.hu.xpi")));
        Assert.False(Directory.Exists(Path.Combine(root, "Profiles", "m.masik")));
        Assert.Contains(result.Messages, m => m.Contains("print-token"));
    }

    [Fact]
    public void Status_is_ok_after_install_and_detects_a_changed_token()
    {
        using var tmp = new TempDir();
        var tb = CreateThunderbird(tmp);
        var settings = Settings(tmp);
        var xpi = CreateXpi(tmp.Combine("x.xpi"));
        var installer = Installer(tmp, tb, settings: settings);

        Assert.False(installer.GetStatus(xpi).Ok);
        installer.Install(new InstallOptions(XpiPath: xpi));
        var ok = installer.GetStatus(xpi);
        Assert.True(ok.Ok);
        Assert.Equal("140.5.0esr", ok.Installations.Single().Installation.Version);

        settings.Settings.Server.Token = EfMailAgent.Configuration.TokenGenerator.NewToken();
        var changed = installer.GetStatus(xpi);
        Assert.False(changed.Ok);
        Assert.False(changed.Installations.Single().TokenOk);
        Assert.True(changed.Installations.Single().InstallUrlOk);
    }

    [Fact]
    public void Status_text_is_hungarian()
    {
        using var tmp = new TempDir();
        var tb = CreateThunderbird(tmp);
        var installer = Installer(tmp, tb);
        var xpi = CreateXpi(tmp.Combine("x.xpi"));
        installer.Install(new InstallOptions(XpiPath: xpi));
        var output = new StringWriter();

        EfMailAgent.Cli.AddonCommands.PrintStatus(installer.GetStatus(xpi), output);

        Assert.Contains("Állapot: RENDBEN", output.ToString());
        Assert.Contains("Token:                 rendben", output.ToString());
    }
}
