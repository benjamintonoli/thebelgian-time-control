using TheBelgian.TimeControl.Core.Payroll.Findings;
using TheBelgian.TimeControl.Core.Payroll.Review;

namespace TheBelgian.TimeControl.Web.Pages.Admin.Payroll;

/// <summary>
/// Resolves Control Center navigation pages from any host page (including home /).
/// Relative "./X" paths only work under /Admin/Payroll/*.
/// </summary>
public static class PayrollNavUrls
{
    public static string AbsolutePage(string navigationPage)
    {
        if (string.IsNullOrWhiteSpace(navigationPage))
        {
            return "/Admin/Payroll/Queue";
        }

        if (navigationPage.StartsWith("/Admin/", StringComparison.Ordinal))
        {
            return navigationPage;
        }

        if (navigationPage.StartsWith("./", StringComparison.Ordinal))
        {
            return "/Admin/Payroll/" + navigationPage[2..];
        }

        return "/Admin/Payroll/" + navigationPage.TrimStart('/');
    }

    public static object CardRouteValues(PayrollControlCard card, int year, int month) =>
        card.Category is PayrollReviewCategory.MissingPerformance or PayrollReviewCategory.WrongDossier
            ? new { year, month, Category = card.Category, Scope = PayrollReviewQueueScope.Open }
            : new { year, month, Scope = PayrollReviewQueueScope.Open };

    public static object PriorityRouteValues(PayrollControlPriorityItem item, int year, int month) =>
        item.Category is PayrollReviewCategory.MissingPerformance or PayrollReviewCategory.WrongDossier
            ? new
            {
                year,
                month,
                Focus = item.FocusKey ?? item.AdminCaseKey,
                Scope = PayrollReviewQueueScope.Open,
                Category = item.Category,
            }
            : new
            {
                year,
                month,
                Focus = item.FocusKey ?? item.AdminCaseKey,
                Scope = PayrollReviewQueueScope.Open,
            };

    public static string CardSubtitle(PayrollControlCard card)
    {
        var open = card.Open + card.FollowUp;
        if (open == 0)
        {
            return "Geen openstaande cases";
        }

        if (card.FollowUp > 0)
        {
            return $"{card.Open} open · {card.FollowUp} opvolging";
        }

        return $"{card.Open} te beoordelen";
    }

    public static string CardToneClass(PayrollControlCard card)
    {
        var open = card.Open + card.FollowUp;
        if (open == 0)
        {
            return "tc-tone-muted";
        }

        return card.Category switch
        {
            PayrollReviewCategory.MissingPerformance => "tc-tone-attention",
            PayrollReviewCategory.WrongDossier => "tc-tone-attention",
            PayrollReviewCategory.Overlap => "tc-tone-attention",
            PayrollReviewCategory.Standby => "tc-tone-primary",
            _ => "tc-tone-primary",
        };
    }
}
