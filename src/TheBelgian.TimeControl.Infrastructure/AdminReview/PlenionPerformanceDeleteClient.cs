using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TheBelgian.TimeControl.Infrastructure.Configuration;

namespace TheBelgian.TimeControl.Infrastructure.AdminReview;

internal sealed record PlenionPerformanceDeleteCommand(
    string ActionId,
    string? IdempotencyKey,
    bool DryRun,
    long PerformanceId,
    long ExpectedResourceId,
    DateOnly ExpectedDate,
    long ExpectedProjectId,
    long? ExpectedBonNr,
    TimeSpan ExpectedStart,
    TimeSpan ExpectedEnd,
    long ExpectedMainTaskId,
    string Reason,
    string ReviewedBy,
    string ReviewCaseId);

internal sealed record PlenionPerformanceDeleteResponse(
    string Status,
    string Message,
    string Reference,
    bool Deleted,
    bool AlreadyApplied,
    bool DryRun,
    long? DeletedPerformanceId);

internal interface IPlenionPerformanceDeleteClient
{
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken);

    Task<PlenionPerformanceDeleteResponse> DeleteAsync(
        PlenionPerformanceDeleteCommand command,
        CancellationToken cancellationToken);
}

/// <summary>
/// Posts delete requests to PlenionWriteService using the same BaseUrl as correction writes.
/// </summary>
internal sealed class HttpPlenionPerformanceDeleteClient(
    HttpClient httpClient,
    IOptions<TimeControlCorrectionWriteOptions> options) : IPlenionPerformanceDeleteClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly TimeControlCorrectionWriteOptions _options = options.Value;

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled || string.IsNullOrWhiteSpace(_options.BaseUrl))
        {
            return false;
        }

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

    public async Task<PlenionPerformanceDeleteResponse> DeleteAsync(
        PlenionPerformanceDeleteCommand command,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post, "api/time-control/performance-deletions")
        {
            Content = JsonContent.Create(command)
        };
        AddApiKey(request);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var result = JsonSerializer.Deserialize<PlenionPerformanceDeleteResponse>(body, JsonOptions);
        if (result is not null)
        {
            return result;
        }

        return new PlenionPerformanceDeleteResponse(
            response.StatusCode == HttpStatusCode.Conflict ? "conflict" : "failed",
            $"PlenionWriteService antwoordde met HTTP {(int)response.StatusCode}.",
            string.Empty,
            Deleted: false,
            AlreadyApplied: false,
            DryRun: command.DryRun,
            DeletedPerformanceId: null);
    }

    private void AddApiKey(HttpRequestMessage request)
    {
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            request.Headers.TryAddWithoutValidation(_options.ApiKeyHeaderName, _options.ApiKey);
        }
    }
}

internal sealed class MockPlenionPerformanceDeleteClient : IPlenionPerformanceDeleteClient
{
    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(true);

    public Task<PlenionPerformanceDeleteResponse> DeleteAsync(
        PlenionPerformanceDeleteCommand command,
        CancellationToken cancellationToken) =>
        Task.FromResult(new PlenionPerformanceDeleteResponse(
            "success",
            "Prestatie lokaal gesimuleerd verwijderd.",
            "mock-delete-" + (command.IdempotencyKey ?? command.ActionId),
            Deleted: true,
            AlreadyApplied: false,
            DryRun: command.DryRun,
            DeletedPerformanceId: command.PerformanceId));
}
