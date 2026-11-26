using System.Text.Json.Nodes;
using EfMailAgent.Cli;
using EfMailAgent.Service;

namespace EfMailAgent.Tests.Service;

public class ServiceManagerTests
{
    private sealed class FakeRunner : IProcessRunner
    {
        public List<(string File, IReadOnlyList<string> Args)> Calls { get; } = [];
        public Func<string, IReadOnlyList<string>, ProcessResult> Handler { get; set; } = (_, _) => new ProcessResult(0, "[SC] SUCCESS");

        public ProcessResult Run(string fileName, IReadOnlyList<string> arguments)
        {
            Calls.Add((fileName, arguments));
            return Handler(fileName, arguments);
        }
    }

    private sealed class FakeControl(bool exists) : IServiceControl
    {
        public bool Installed { get; set; } = exists;
        public List<string> Actions { get; } = [];

        public bool Exists(string name) => Installed;
        public string Status(string name) => "Running";
        public void Start(string name, TimeSpan timeout) => Actions.Add("start");
        public void Stop(string name, TimeSpan timeout) => Actions.Add("stop");
    }

    [Fact]
    public void New_service_is_created_with_delayed_start_recovery_and_started()
    {
        var runner = new FakeRunner();
        var control = new FakeControl(exists: false);

        var result = new ServiceManager(runner, control).Install(new ServiceInstallOptions(@"C:\Program Files\EfMailAgent\EfMailAgent.exe", @"C:\ProgramData\EfMailAgent"));

        Assert.True(result.Success, string.Join("\n", result.Messages));
        Assert.Equal(["create", "EfMailAgent", "binPath=", "\"C:\\Program Files\\EfMailAgent\\EfMailAgent.exe\"", "start=", "delayed-auto",
            "DisplayName=", "EfMailAgent e-mail AI feldolgozó", "obj=", "LocalSystem"], runner.Calls[0].Args);
        Assert.Equal("description", runner.Calls[1].Args[0]);
        Assert.Equal(["failure", "EfMailAgent", "reset=", "86400", "actions=", "restart/60000/restart/60000/restart/60000"], runner.Calls[2].Args);
        Assert.Equal(["failureflag", "EfMailAgent", "1"], runner.Calls[3].Args);
        Assert.DoesNotContain(runner.Calls, c => c.File == "icacls.exe");
        Assert.Equal(["start"], control.Actions);
    }

    [Fact]
    public void Existing_service_is_reconfigured_and_custom_account_gets_folder_rights()
    {
        var runner = new FakeRunner();
        var control = new FakeControl(exists: true);

        var result = new ServiceManager(runner, control).Install(new ServiceInstallOptions(@"C:\EfMailAgent.exe", @"C:\ProgramData\EfMailAgent", "efservice", "titok"));

        Assert.True(result.Success);
        Assert.Equal("config", runner.Calls[0].Args[0]);
        Assert.Contains(@".\efservice", runner.Calls[0].Args);
        Assert.Equal("titok", runner.Calls[0].Args[^1]);
        var icacls = runner.Calls.Single(c => c.File == "icacls.exe");
        Assert.Equal([@"C:\ProgramData\EfMailAgent", "/grant", @".\efservice:(OI)(CI)M", "/T", "/C", "/Q"], icacls.Args);
    }

    [Fact]
    public void Upgrade_without_account_keeps_the_current_account()
    {
        var runner = new FakeRunner();

        var result = new ServiceManager(runner, new FakeControl(exists: true)).Install(new ServiceInstallOptions(@"C:\EfMailAgent.exe", @"C:\Data"));

        Assert.True(result.Success);
        Assert.Equal(["config", "EfMailAgent", "binPath=", "\"C:\\EfMailAgent.exe\"", "start=", "delayed-auto", "DisplayName=", "EfMailAgent e-mail AI feldolgozó"], runner.Calls[0].Args);
        Assert.DoesNotContain(runner.Calls, c => c.File == "icacls.exe");
    }

    [Fact]
    public void Sc_failure_is_reported_without_the_password()
    {
        var runner = new FakeRunner { Handler = (_, _) => new ProcessResult(1057, "[SC] ChangeServiceConfig FAILED 1057: The account name is invalid") };

        var result = new ServiceManager(runner, new FakeControl(false)).Install(new ServiceInstallOptions("x.exe", "d", @"TARTOMANY\rossz", "titkos-jelszo"));

        Assert.False(result.Success);
        var message = result.Messages.Single();
        Assert.Contains("1057", message);
        Assert.Contains("***", message);
        Assert.DoesNotContain("titkos-jelszo", message);
    }

    [Fact]
    public void Uninstall_stops_then_deletes_and_is_idempotent()
    {
        var runner = new FakeRunner();
        var control = new FakeControl(exists: true);

        var result = new ServiceManager(runner, control).Uninstall();

        Assert.True(result.Success);
        Assert.Equal(["stop"], control.Actions);
        Assert.Equal(["delete", "EfMailAgent"], runner.Calls.Single().Args);
        Assert.True(new ServiceManager(runner, new FakeControl(false)).Uninstall().Success);
    }

    [Theory]
    [InlineData("efservice", @".\efservice")]
    [InlineData(@"CEG\efservice", @"CEG\efservice")]
    [InlineData("efservice@ceg.local", "efservice@ceg.local")]
    [InlineData("localsystem", "LocalSystem")]
    public void Account_names_are_normalized(string input, string expected) =>
        Assert.Equal(expected, ServiceManager.NormalizeAccount(input));

    [Fact]
    public void Without_windows_service_control_a_hungarian_message_is_returned()
    {
        var result = new ServiceManager(new FakeRunner(), null).Install(new ServiceInstallOptions("x", "y"));
        Assert.Equal(2, result.ExitCode);
        Assert.Contains("csak Windowson", result.Messages.Single());
    }
}

[Collection("Console")]
public class ConfigureCommandTests
{
    [Fact]
    public async Task Configure_creates_the_data_folder_and_sets_only_the_given_values()
    {
        using var tmp = new TempDir();
        var data = tmp.Combine("data");

        var first = await CliApp.RunAsync(["configure", "--data-dir", data, "--llm-provider", "ollama", "--llm-base-url", "http://10.0.0.5:11434",
            "--llm-model", "qwen3.5:9b", "--output", tmp.Combine("kimenet")], new StringWriter(), new StringWriter());

        Assert.Equal(ExitCodes.Ok, first);
        var settings = JsonNode.Parse(File.ReadAllText(Path.Combine(data, "appsettings.json")))!;
        var token = settings["Server"]!["Token"]!.GetValue<string>();
        Assert.Equal(43, token.Length);
        Assert.Equal("ollama", settings["Llm"]!["Provider"]!.GetValue<string>());
        Assert.Equal("qwen3.5:9b", settings["Llm"]!["Model"]!.GetValue<string>());
        Assert.True(Directory.Exists(tmp.Combine("kimenet")));

        // Upgrade: a run without options changes nothing (token kept).
        Assert.Equal(ExitCodes.Ok, await CliApp.RunAsync(["configure", "--data-dir", data], new StringWriter(), new StringWriter()));
        var again = JsonNode.Parse(File.ReadAllText(Path.Combine(data, "appsettings.json")))!;
        Assert.Equal(token, again["Server"]!["Token"]!.GetValue<string>());
        Assert.Equal("http://10.0.0.5:11434", again["Llm"]!["BaseUrl"]!.GetValue<string>());

        var stderr = new StringWriter();
        Assert.Equal(ExitCodes.ConfigurationError, await CliApp.RunAsync(["configure", "--data-dir", data, "--llm-provider", "gpt"], new StringWriter(), stderr));
        Assert.Contains("Llm.Provider ismeretlen", stderr.ToString());
        Assert.Equal("ollama", JsonNode.Parse(File.ReadAllText(Path.Combine(data, "appsettings.json")))!["Llm"]!["Provider"]!.GetValue<string>());
    }
}
