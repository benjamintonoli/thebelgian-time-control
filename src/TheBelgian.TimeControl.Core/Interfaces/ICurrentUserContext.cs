using TheBelgian.TimeControl.Core.Models;

namespace TheBelgian.TimeControl.Core.Interfaces;

public interface ICurrentUserContext
{
    AuthenticatedActor? CurrentUser { get; }

    AuthenticatedActor RequireActor(string developmentFallbackReviewer);
}
