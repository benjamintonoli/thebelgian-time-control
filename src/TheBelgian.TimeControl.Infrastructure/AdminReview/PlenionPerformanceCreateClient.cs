using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TheBelgian.TimeControl.Infrastructure.Configuration;

namespace TheBelgian.TimeControl.Infrastructure.AdminReview;

internal sealed record PlenionPerformanceCreateCommand(
    string ActionId,
    string ResourceId,
    DateOnly Date,
    TimeSpan Start,
    TimeSpan End,
    string ProjectId,
    string? BonNr,
    int MainTaskId,
    string Reason,
    string ReviewedBy,
    string IdempotencyKey);

internal sealed record PlenionPerformanceCreateResponse(
    string Status,
    string Message,
    string Reference,
    string IdempotencyKey,
    long? PerformanceId,
    string? ResourceId,
    DateOnly? Date,
    TimeSpan? Start,
    TimeSpan? End,
    string? ProjectId,
    string? BonNr,
    int? MainTaskId);

internal interface IPlenionPerformanceCreateClient
{
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken);

    Task<PlenionPerformanceCreateResponse> CreateAsync(
        PlenionPerformanceCreateCommand command,
        CancellationToken cancellationToken);
}

/// <summary>
/// Posts create requests to PlenionWriteService using the same BaseUrl as correction writes.
/// When the create contract is not proven server-side, PWS should return status contract_unproven.
/// </summary>
internal sealed class HttpPlenionPerformanceCreateClient(
    HttpClient httpClient,
    IOptions<TimeControlCorrectionWriteOptions> options) : IPlenionPerformanceCreateClient
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

    public async Task<PlenionPerformanceCreateResponse> CreateAsync(
        PlenionPerformanceCreateCommand command,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post, "api/time-control/performance-creations")
        {
            Content = JsonContent.Create(command)
        };
        AddApiKey(request);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var result = JsonSerializer.Deserialize<PlenionPerformanceCreateResponse>(body, JsonOptions);
        if (result is not null)
        {
            return result;
        }

        return new PlenionPerformanceCreateResponse(
            response.StatusCode == HttpStatusCode.Conflict ? "conflict" : "failed",
            $"PlenionWriteService antwoordde met HTTP {(int)response.StatusCode}.",
            string.Empty,
            command.IdempotencyKey,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null);
    }

    private void AddApiKey(HttpRequestMessage request)
    {
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            request.Headers.TryAddWithoutValidation(_options.ApiKeyHeaderName, _options.ApiKey);
        }
    }
}

internal sealed class MockPlenionPerformanceCreateClient : IPlenionPerformanceCreateClient
{
    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(true);

    public Task<PlenionPerformanceCreateResponse> CreateAsync(
        PlenionPerformanceCreateCommand command,
        CancellationToken cancellationToken) =>
        Task.FromResult(new PlenionPerformanceCreateResponse(
            "success",
            "Prestatie lokaal gesimuleerd.",
            "mock-create-" + command.IdempotencyKey,
            command.IdempotencyKey,
            9_000_001,
            command.ResourceId,
            command.Date,
            command.Start,
            command.End,
            command.ProjectId,
            command.BonNr,
            command.MainTaskId));
}

/// <summary>
/// Used when write BaseUrl is configured but the create contract is not yet activated.
/// Returns contract_unproven so TimeControl marks Failed instead of pretending Applied.
/// </summary>
internal sealed class UnprovenPlenionPerformanceCreateClient : IPlenionPerformanceCreateClient
{
    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(true);

    public Task<PlenionPerformanceCreateResponse> CreateAsync(
        PlenionPerformanceCreateCommand command,
        CancellationToken cancellationToken) =>
        Task.FromResult(new PlenionPerformanceCreateResponse(
            "contract_unproven",
            "PWS performance-create contract is nog niet bewezen/actief; create geweigerd.",
            string.Empty,
            command.IdempotencyKey,
            null,
            command.ResourceId,
            command.Date,
            command.Start,
            command.End,
            command.ProjectId,
            command.BonNr,
            command.MainTaskId));
}
