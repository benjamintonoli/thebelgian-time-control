using TheBelgian.TimeControl.Core.Models;

namespace TheBelgian.TimeControl.Core.Interfaces;

/// <summary>
/// AI-ready explanation surface. MVP uses a deterministic implementation only.
/// </summary>
public interface IReviewExplanationService
{
    string Explain(SourceEvidence source, MatcherAssessment matcher);
}
