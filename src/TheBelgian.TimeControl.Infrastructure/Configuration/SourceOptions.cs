using System.ComponentModel.DataAnnotations;

namespace TheBelgian.TimeControl.Infrastructure.Configuration;

public sealed class PlenionOptions
{
    public const string SectionName = "ConnectionStrings";
    public string PlenionOdbc { get; init; } = string.Empty;
}

public sealed class PowerfleetOptions
{
    public const string SectionName = "Powerfleet";

    [Url]
    public string BaseUrl { get; init; } = string.Empty;

    public string ApiKey { get; init; } = string.Empty;
    public string ReportId { get; init; } = string.Empty;
    public string StateId { get; init; } = string.Empty;

    public bool IsConfigured =>
        Uri.TryCreate(BaseUrl, UriKind.Absolute, out _) &&
        !string.IsNullOrWhiteSpace(ApiKey) &&
        !string.IsNullOrWhiteSpace(ReportId) &&
        !string.IsNullOrWhiteSpace(StateId);
}

public sealed class VehicleAssignmentReviewOptions
{
    public const string SectionName = "VehicleAssignments";
    public string DefaultReviewer { get; init; } = string.Empty;
}

public sealed class AdminReviewWorkflowOptions
{
    public const string SectionName = "AdminReview";
    public string DefaultReviewer { get; init; } = string.Empty;
}
