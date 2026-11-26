using System.Globalization;
using EfMailAgent.Jobs;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EfMailAgent.Processing;

public sealed record DailySummary(DateOnly Day, int Processed, int Done, int NeedsReview, int Failed, double? AverageSeconds, int QueueLength)
{
    public string ToHungarian() => string.Create(CultureInfo.GetCultureInfo("hu-HU"),
        $"Napi összesítő ({Day:yyyy-MM-dd}): feldolgozva {Processed} (kész {Done}, ellenőrzendő {NeedsReview}, hiba {Failed}), " +
        $"átlagos feldolgozási idő {(AverageSeconds is { } s ? $"{s:0.0} mp" : "–")}, jelenleg a sorban {QueueLength}");
}

/// <summary>Writes one summary line per day into the log, shortly after local midnight (App.TimeZone).</summary>
public sealed class DailySummaryService(JobQueue queue, TimeZoneInfo timeZone, TimeProvider timeProvider, ILogger<DailySummaryService> logger)
    : BackgroundService
{
    public DailySummary Build(DateOnly day)
    {
        var start = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), timeZone.GetUtcOffset(day.ToDateTime(TimeOnly.MinValue)));
        var nextDay = day.AddDays(1).ToDateTime(TimeOnly.MinValue);
        var end = new DateTimeOffset(nextDay, timeZone.GetUtcOffset(nextDay));
        var stats = queue.FinishedBetween(start, end);
        return new DailySummary(day, stats.Done + stats.NeedsReview + stats.Failed, stats.Done, stats.NeedsReview, stats.Failed,
            stats.AverageDurationMs / 1000.0, queue.QueueLength());
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var nowLocal = TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), timeZone);
            var nextMidnight = new DateTimeOffset(nowLocal.Date.AddDays(1), nowLocal.Offset).AddMinutes(1);
            try
            {
                await Task.Delay(nextMidnight - nowLocal, timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                logger.LogInformation("{Summary}", Build(DateOnly.FromDateTime(nowLocal.Date)).ToHungarian());
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "A napi összesítő nem készült el.");
            }
        }
    }
}
