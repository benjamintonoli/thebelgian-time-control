namespace TheBelgian.TimeControl.Infrastructure.Configuration;

public sealed class TimeControlCorrectionWriteOptions
{
    public const string SectionName = "TimeControlCorrectionWrites";
    public bool Enabled { get; init; }
    public bool UseMock { get; init; }
    public string BaseUrl { get; init; } = string.Empty;
    public int TimeoutSeconds { get; init; } = 30;
    public string ApiKeyHeaderName { get; init; } = "X-Api-Key";
    public string ApiKey { get; init; } = string.Empty;
}
