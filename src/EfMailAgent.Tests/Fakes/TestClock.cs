namespace EfMailAgent.Tests.Fakes;

/// <summary>A controllable clock; starts at a fixed instant.</summary>
public sealed class TestClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public static DateTimeOffset DefaultStart => new(2026, 11, 26, 8, 30, 0, TimeSpan.Zero);

    public TestClock() : this(DefaultStart)
    {
    }

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now = _now.Add(by);
}
