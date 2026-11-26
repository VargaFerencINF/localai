using EfMailAgent.Configuration;
using EfMailAgent.Jobs;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EfMailAgent.Processing;

/// <summary>
/// The single background worker: strictly one job at a time, FIFO. Recovers interrupted jobs on start, wakes up on new
/// jobs, pauses (without using up attempts) while the LLM server is unreachable.
/// </summary>
public sealed class JobWorker(JobQueue queue, JobProcessor processor, EffectiveSettings settings, ILogger<JobWorker>? logger = null)
    : BackgroundService
{
    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;
    private readonly SemaphoreSlim _signal = new(0);
    private bool _inOutage;

    /// <summary>True while the worker waits for the LLM server to come back.</summary>
    public bool InOutage => _inOutage;

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        var recovered = queue.RecoverInterrupted();
        if (recovered > 0)
            _logger.LogWarning("{Count} megszakadt feladat visszakerült a sorba (a szolgáltatás nem szabályosan állt le).", recovered);
        queue.JobAvailable += OnJobAvailable;
        return base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        queue.JobAvailable -= OnJobAvailable;
        await base.StopAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        _logger.LogInformation("A feldolgozó elindult (LLM: {Provider}).", processor.Llm.Provider);
        var idle = TimeSpan.FromMilliseconds(settings.Settings.Worker.IdlePollMilliseconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            Job? job;
            try
            {
                job = queue.DequeueNext();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "A sor olvasása nem sikerült, újrapróbálás hamarosan.");
                await WaitAsync(TimeSpan.FromSeconds(5), stoppingToken);
                continue;
            }

            if (job is null)
            {
                var next = queue.NextScheduledAttempt();
                var wait = next is null ? idle : TimeSpan.FromTicks(Math.Clamp((next.Value - DateTimeOffset.UtcNow).Ticks, 0, idle.Ticks));
                await WaitAsync(wait, stoppingToken);
                continue;
            }

            ProcessResult result;
            try
            {
                result = await processor.ProcessAsync(job, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Váratlan hiba a(z) {JobId} feldolgozása közben.", job.Id);
                queue.Complete(job.Id, new JobCompletion(JobStatus.Failed, $"Váratlan hiba: {ex.Message}", null, null, null, null, null, null));
                continue;
            }
            catch (OperationCanceledException)
            {
                queue.Release(job.Id, "A szolgáltatás leállt feldolgozás közben, a feladat visszakerült a sorba.", null);
                break;
            }

            if (result.Result == AttemptResult.PausedForOutage)
            {
                if (!_inOutage)
                    _logger.LogWarning("Az LLM nem érhető el, a feldolgozás szünetel (a feladatok a sorban maradnak): {Error}", result.Error);
                else
                    _logger.LogDebug("Az LLM továbbra sem érhető el: {Error}", result.Error);
                _inOutage = true;
                await WaitAsync(TimeSpan.FromSeconds(settings.Settings.Worker.LlmOutagePauseSeconds), stoppingToken, wakeOnSignal: false);
            }
            else if (_inOutage && result.Transport is null)
            {
                _inOutage = false;
                _logger.LogInformation("Az LLM újra elérhető, a feldolgozás folytatódik.");
            }
        }
    }

    private void OnJobAvailable(object? sender, EventArgs e)
    {
        if (_signal.CurrentCount == 0)
            _signal.Release();
    }

    private async Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken, bool wakeOnSignal = true)
    {
        try
        {
            if (wakeOnSignal)
                await _signal.WaitAsync(delay, cancellationToken);
            else
                await Task.Delay(delay, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public override void Dispose()
    {
        _signal.Dispose();
        base.Dispose();
    }
}
