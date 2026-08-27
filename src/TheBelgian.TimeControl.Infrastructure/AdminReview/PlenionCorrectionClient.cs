using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TheBelgian.TimeControl.Infrastructure.Configuration;

namespace TheBelgian.TimeControl.Infrastructure.AdminReview;

internal sealed record PlenionCorrectionCommand(
    IReadOnlyList<PlenionCorrectionItem> Corrections,
    string Reason,
    string ReviewedBy,
    string ReviewCaseId,
    string IdempotencyKey);

internal sealed record PlenionCorrectionItem(
    long PerformanceId,
    TimeSpan OriginalStart,
    TimeSpan OriginalEnd,
    TimeSpan? NewStart,
    TimeSpan? NewEnd,
    string ExpectedActivityType,
    long? ExpectedMainTaskExternalId);

internal sealed record PlenionCorrectionResponse(
    string Status,
    string Message,
    string Reference,
    string IdempotencyKey,
    IReadOnlyList<PlenionCorrectionResultItem> Performances);

internal sealed record PlenionCorrectionResultItem(
    long PerformanceId,
    TimeSpan? PreviousStart,
    TimeSpan? PreviousEnd,
    TimeSpan? CurrentStart,
    TimeSpan? CurrentEnd);

internal interface IPlenionCorrectionClient
{
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken);
    Task<PlenionCorrectionResponse> ExecuteAsync(
        PlenionCorrectionCommand command,
        CancellationToken cancellationToken);
}

internal sealed class HttpPlenionCorrectionClient(
    HttpClient httpClient,
    IOptions<TimeControlCorrectionWriteOptions> options) : IPlenionCorrectionClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly TimeControlCorrectionWriteOptions _options = options.Value;

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled || string.IsNullOrWhiteSpace(_options.BaseUrl)) return false;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "health");
            AddApiKey(request);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    public async Task<PlenionCorrectionResponse> ExecuteAsync(
        PlenionCorrectionCommand command,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post, "api/time-control/performance-corrections")
        {
            Content = JsonContent.Create(command)
        };
        AddApiKey(request);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var result = JsonSerializer.Deserialize<PlenionCorrectionResponse>(body, JsonOptions);
        if (result is not null) return result;
        return new PlenionCorrectionResponse(
            response.StatusCode == HttpStatusCode.Conflict ? "conflict" : "failed",
            $"PlenionWriteService antwoordde met HTTP {(int)response.StatusCode}.",
            string.Empty,
            command.IdempotencyKey,
            []);
    }

    private void AddApiKey(HttpRequestMessage request)
    {
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
            request.Headers.TryAddWithoutValidation(_options.ApiKeyHeaderName, _options.ApiKey);
    }
}

internal sealed class MockPlenionCorrectionClient : IPlenionCorrectionClient
{
    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(true);

    public Task<PlenionCorrectionResponse> ExecuteAsync(
        PlenionCorrectionCommand command,
        CancellationToken cancellationToken) => Task.FromResult(new PlenionCorrectionResponse(
        "success",
        "Correctie lokaal gesimuleerd en teruggelezen.",
        "mock-" + command.IdempotencyKey,
        command.IdempotencyKey,
        command.Corrections.Select(item => new PlenionCorrectionResultItem(
            item.PerformanceId,
            item.OriginalStart,
            item.OriginalEnd,
            item.NewStart ?? item.OriginalStart,
            item.NewEnd ?? item.OriginalEnd)).ToArray()));
}
