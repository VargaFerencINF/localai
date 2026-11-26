using EfMailAgent.Addon;
using static EfMailAgent.Tests.Addon.AddonTestSupport;

namespace EfMailAgent.Tests.Addon;

public class SelfHealTests
{
    [Fact]
    public void Does_nothing_before_install_addon_ran()
    {
        using var tmp = new TempDir();
        var tb = CreateThunderbird(tmp);
        var installer = new AddonInstaller(Settings(tmp), new FixedThunderbirdLocator(tb), new FakeElevation(true));

        var result = installer.SelfHeal();

        Assert.True(result.Success);
        Assert.False(File.Exists(PolicyFile.PolicyPath(tb)));
    }

    [Fact]
    public void Repairs_a_missing_entry_or_a_changed_token()
    {
        using var tmp = new TempDir();
        var tb = CreateThunderbird(tmp);
        var settings = Settings(tmp);
        var xpi = CreateXpi(tmp.Combine("dist", "efmailagent.xpi"));
        // The service is not elevated by the check, but runs as LocalSystem: self-heal must not require the check.
        AddonInstaller Installer() => new(settings, new FixedThunderbirdLocator(tb), new FakeElevation(false), bundledXpiPath: xpi);
        Assert.True(new AddonInstaller(settings, new FixedThunderbirdLocator(tb), new FakeElevation(true)).Install(new InstallOptions(XpiPath: xpi)).Success);
        Assert.Contains("rendben", Installer().SelfHeal().Messages.Single());

        // A Thunderbird update wiped the distribution folder:
        File.Delete(PolicyFile.PolicyPath(tb));
        var repaired = Installer().SelfHeal();
        Assert.True(repaired.Success, string.Join("\n", repaired.Messages));
        Assert.NotNull(PolicyFile.Read(PolicyFile.Load(PolicyFile.PolicyPath(tb)).Root, AddonPackage.ExpectedId));

        // The token was regenerated:
        settings.Settings.Server.Token = EfMailAgent.Configuration.TokenGenerator.NewToken();
        Installer().SelfHeal();
        Assert.Equal(settings.Settings.Server.Token, PolicyFile.Read(PolicyFile.Load(PolicyFile.PolicyPath(tb)).Root, AddonPackage.ExpectedId)!.Token);

        // A newer bundled version replaces the installed XPI:
        CreateXpi(xpi, "0.9.0");
        Installer().SelfHeal();
        Assert.EndsWith("efmailagent-0.9.0.xpi", PolicyFile.Read(PolicyFile.Load(PolicyFile.PolicyPath(tb)).Root, AddonPackage.ExpectedId)!.InstallUrl);
    }

    [Fact]
    public void Never_throws_without_thunderbird_or_with_a_broken_state_file()
    {
        using var tmp = new TempDir();
        var settings = Settings(tmp);
        Directory.CreateDirectory(settings.AddonDir);
        File.WriteAllText(Path.Combine(settings.AddonDir, AddonInstaller.StateFileName), """{"mode":"policy","version":"0.1.0","thunderbirdDirs":["/nincs"],"installedAt":"2026-11-26T10:00:00Z"}""");

        var noThunderbird = AddonSelfHealService.CreateInstaller(settings).SelfHeal();
        Assert.NotNull(noThunderbird);

        File.WriteAllText(Path.Combine(settings.AddonDir, AddonInstaller.StateFileName), "{ hibás");
        Assert.True(AddonSelfHealService.CreateInstaller(settings).SelfHeal().Success);
    }

    [Fact]
    public async Task Hosted_service_starts_in_the_background()
    {
        using var tmp = new TempDir();
        var service = new AddonSelfHealService(Settings(tmp), Microsoft.Extensions.Logging.Abstractions.NullLogger<AddonInstaller>.Instance);

        await service.StartAsync(CancellationToken.None);
        await service.Running!.WaitAsync(TimeSpan.FromSeconds(10));
        await service.StopAsync(CancellationToken.None);
    }
}
