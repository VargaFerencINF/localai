using EfMailAgent.Configuration;
using EfMailAgent.Jobs;
using EfMailAgent.Processing;
using EfMailAgent.Tests.Fakes;

namespace EfMailAgent.Tests.Processing;

public class JobWorkerTests : IAsyncLifetime
{
    private FakeLlmServer _server = null!;

    public async Task InitializeAsync() => _server = await FakeLlmServer.StartAsync();

    public async Task DisposeAsync() => await _server.DisposeAsync();

    [Fact]
    public async Task Worker_processes_all_jobs_strictly_one_at_a_time_in_fifo_order()
    {
        _server.Handler = (_, _) => new FakeReply(LlmAnswers.ValidOneRow, Delay: TimeSpan.FromMilliseconds(150));
        using var h = new PipelineHarness(s =>
        {
            s.Llm.Provider = LlmProviders.Ollama;
            s.Llm.BaseUrl = _server.BaseUrl;
        });
        var jobs = Enumerable.Range(1, 4).Select(i => h.Enqueue(subject: $"Előrendelés {i}", body: $"levél {i}")).ToList();
        using var worker = new JobWorker(h.Queue, h.Processor, h.Settings);

        await worker.StartAsync(CancellationToken.None);
        await WaitUntil(() => jobs.All(j => JobStatus.IsTerminal(h.Queue.Get(j.Id)!.Status)), TimeSpan.FromSeconds(20));
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(1, _server.MaxConcurrency);
        Assert.All(jobs, j => Assert.Equal(JobStatus.Done, h.Queue.Get(j.Id)!.Status));
        var order = _server.ChatRequests.Select(r => r["messages"]![1]!["content"]!.GetValue<string>()).ToList();
        for (var i = 0; i < jobs.Count; i++)
            Assert.Contains($"levél {i + 1}", order[i]);
        Assert.Equal(4, File.ReadAllLines(Assert.Single(h.CsvFiles())).Length - 1);
    }

    [Fact]
    public async Task Worker_wakes_up_for_a_job_enqueued_later()
    {
        _server.Handler = (_, _) => new FakeReply(LlmAnswers.ValidOneRow);
        using var h = new PipelineHarness(s =>
        {
            s.Llm.Provider = LlmProviders.Ollama;
            s.Llm.BaseUrl = _server.BaseUrl;
            s.Worker.IdlePollMilliseconds = 60_000; // only the enqueue signal can wake it in time
        });
        using var worker = new JobWorker(h.Queue, h.Processor, h.Settings);
        await worker.StartAsync(CancellationToken.None);
        await Task.Delay(200);

        var job = h.Enqueue();
        await WaitUntil(() => h.Queue.Get(job.Id)!.Status == JobStatus.Done, TimeSpan.FromSeconds(10));
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Stopping_mid_job_puts_it_back_without_using_an_attempt()
    {
        _server.Handler = (_, _) => new FakeReply(LlmAnswers.ValidOneRow, Delay: TimeSpan.FromSeconds(30));
        using var h = new PipelineHarness(s =>
        {
            s.Llm.Provider = LlmProviders.Ollama;
            s.Llm.BaseUrl = _server.BaseUrl;
        });
        var job = h.Enqueue();
        using var worker = new JobWorker(h.Queue, h.Processor, h.Settings);
        await worker.StartAsync(CancellationToken.None);
        await WaitUntil(() => _server.CallCount == 1, TimeSpan.FromSeconds(10));

        await worker.StopAsync(CancellationToken.None);

        var after = h.Queue.Get(job.Id)!;
        Assert.Equal(JobStatus.Queued, after.Status);
        Assert.Equal(0, after.Attempts);
        Assert.Contains("leállt", after.LastError);
    }

    [Fact]
    public async Task Restart_recovers_jobs_left_in_processing()
    {
        using var h = new PipelineHarness();
        var job = h.Enqueue();
        Assert.Equal(JobStatus.Processing, h.Queue.DequeueNext()!.Status); // simulated crash: never completed

        var restartedQueue = new JobQueue(new JobDatabase(h.Settings.DataDir), h.Clock);
        var processor = new JobProcessor(h.Rules, restartedQueue, h.Llm, h.Csv, h.Settings, h.TimeZone, h.Clock);
        using var worker = new JobWorker(restartedQueue, processor, h.Settings);
        await worker.StartAsync(CancellationToken.None);
        await WaitUntil(() => JobStatus.IsTerminal(restartedQueue.Get(job.Id)!.Status), TimeSpan.FromSeconds(10));
        await worker.StopAsync(CancellationToken.None);

        var done = restartedQueue.Get(job.Id)!;
        Assert.Equal(JobStatus.NeedsReview, done.Status); // mock demo answer contains a low-confidence row
        Assert.Equal(1, done.Attempts);
    }

    [Fact]
    public async Task Outage_pauses_the_worker_and_resumes_when_llm_is_back()
    {
        _server.Handler = (_, _) => new FakeReply(LlmAnswers.ValidOneRow);
        var reachable = false;
        using var h = new PipelineHarness(
            s => s.Worker.LlmOutagePauseSeconds = 1,
            _ => new SwitchableLlm(() => reachable, _server.BaseUrl));
        var job = h.Enqueue();
        using var worker = new JobWorker(h.Queue, h.Processor, h.Settings);
        await worker.StartAsync(CancellationToken.None);

        await WaitUntil(() => worker.InOutage, TimeSpan.FromSeconds(10));
        Assert.Equal(0, h.Queue.Get(job.Id)!.Attempts);

        reachable = true;
        h.Clock.Advance(TimeSpan.FromSeconds(5)); // the job's next attempt time has passed
        await WaitUntil(() => h.Queue.Get(job.Id)!.Status == JobStatus.Done, TimeSpan.FromSeconds(10));
        await worker.StopAsync(CancellationToken.None);
        Assert.False(worker.InOutage);
        Assert.Equal(1, h.Queue.Get(job.Id)!.Attempts);
    }

    internal static async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("A feltétel nem teljesült időben.");
            await Task.Delay(25);
        }
    }

    /// <summary>Ollama client that points at a closed port until <c>reachable()</c> becomes true.</summary>
    private sealed class SwitchableLlm(Func<bool> reachable, string baseUrl) : EfMailAgent.Llm.ILlmClient
    {
        private readonly EfMailAgent.Llm.OllamaClient _down = new(new HttpClient(), "http://127.0.0.1:1");
        private readonly EfMailAgent.Llm.OllamaClient _up = new(new HttpClient(), baseUrl);

        public string Provider => "ollama";

        public Task<EfMailAgent.Llm.LlmResponse> CompleteAsync(EfMailAgent.Llm.LlmRequest request, CancellationToken cancellationToken) =>
            (reachable() ? _up : _down).CompleteAsync(request, cancellationToken);

        public Task<EfMailAgent.Llm.LlmHealth> CheckHealthAsync(CancellationToken cancellationToken) =>
            (reachable() ? _up : _down).CheckHealthAsync(cancellationToken);
    }
}
