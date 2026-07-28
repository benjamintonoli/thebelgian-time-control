namespace TheBelgian.TimeControl.Core.Services;

public readonly record struct TimeInterval(DateTimeOffset Start, DateTimeOffset End)
{
    public TimeSpan Duration =>
        End >= Start
            ? End - Start
            : throw new InvalidOperationException("Eindtijd ligt vóór de starttijd.");

    public TimeSpan Overlap(TimeInterval other)
    {
        var start = Start > other.Start ? Start : other.Start;
        var end = End < other.End ? End : other.End;
        return end > start ? end - start : TimeSpan.Zero;
    }
}
