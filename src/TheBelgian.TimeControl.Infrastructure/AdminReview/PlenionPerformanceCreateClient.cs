using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    string ReviewCaseId,
    string IdempotencyKey,
    TimeSpan? Pause = null,
    bool DryRun = false);

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
        if (!long.TryParse(command.ResourceId, out var resourceId) || resourceId <= 0)
        {
            return Fail(command, "invalid_resource", "ResourceId moet numeriek en positief zijn.");
        }

        if (!long.TryParse(command.ProjectId, out var projectId) || projectId <= 0)
        {
            return Fail(command, "invalid_project", "ProjectId moet numeriek en positief zijn.");
        }

        long? bonNr = null;
        if (!string.IsNullOrWhiteSpace(command.BonNr))
        {
            if (!long.TryParse(command.BonNr, out var parsedBon) || parsedBon <= 0)
            {
                return Fail(command, "invalid_bon", "BonNr moet numeriek en positief zijn wanneer opgegeven.");
            }

            bonNr = parsedBon;
        }

        var payload = new
        {
            command.ActionId,
            command.IdempotencyKey,
            ResourceId = resourceId,
            command.Date,
            command.Start,
            command.End,
            Pause = command.Pause ?? TimeSpan.Zero,
            ProjectId = projectId,
            BonNr = bonNr,
            MainTaskId = (long)command.MainTaskId,
            command.Reason,
            command.ReviewedBy,
            command.ReviewCaseId,
            DryRun = command.DryRun,
        };

        using var request = new HttpRequestMessage(
            HttpMethod.Post, "api/time-control/performance-creations")
        {
            Content = JsonContent.Create(payload, options: JsonOptions)
        };
        AddApiKey(request);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var wire = JsonSerializer.Deserialize<PwsCreateWireResponse>(body, JsonOptions);
        if (wire is null)
        {
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

        var status = wire.Status ?? "failed";
        if (string.Equals(status, "create_contract_unproven", StringComparison.OrdinalIgnoreCase))
        {
            status = "contract_unproven";
        }

        var performanceId = wire.Performance?.IdProjPrest;
        return new PlenionPerformanceCreateResponse(
            status,
            wire.Message ?? string.Empty,
            wire.Reference ?? string.Empty,
            string.IsNullOrWhiteSpace(wire.IdempotencyKey) ? command.IdempotencyKey : wire.IdempotencyKey,
            performanceId,
            wire.Performance?.ResourceId.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? command.ResourceId,
            wire.Performance?.Date ?? command.Date,
            wire.Performance?.Van ?? command.Start,
            wire.Performance?.Tot ?? command.End,
            wire.Performance?.IdProj.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? command.ProjectId,
            wire.Performance?.BonNr?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? command.BonNr,
            wire.Performance is null ? command.MainTaskId : (int)wire.Performance.IdHfdTaak);
    }

    private static PlenionPerformanceCreateResponse Fail(
        PlenionPerformanceCreateCommand command,
        string status,
        string message) =>
        new(
            status,
            message,
            string.Empty,
            command.IdempotencyKey,
            null,
            command.ResourceId,
            command.Date,
            command.Start,
            command.End,
            command.ProjectId,
            command.BonNr,
            command.MainTaskId);

    private void AddApiKey(HttpRequestMessage request)
    {
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            request.Headers.TryAddWithoutValidation(_options.ApiKeyHeaderName, _options.ApiKey);
        }
    }

    private sealed class PwsCreateWireResponse
    {
        public string? Status { get; set; }
        public string? Message { get; set; }
        public string? Reference { get; set; }
        public string? IdempotencyKey { get; set; }
        public string? ActionId { get; set; }
        public bool Created { get; set; }
        public bool AlreadyApplied { get; set; }
        public bool DryRun { get; set; }
        public PwsCreatedPerformance? Performance { get; set; }
    }

    private sealed class PwsCreatedPerformance
    {
        public long? IdProjPrest { get; set; }
        public long ResourceId { get; set; }
        public DateOnly Date { get; set; }
        public TimeSpan Van { get; set; }
        public TimeSpan Tot { get; set; }
        public long IdProj { get; set; }
        public long? BonNr { get; set; }
        public long IdHfdTaak { get; set; }
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
