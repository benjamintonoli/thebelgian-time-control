using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;

namespace TheBelgian.TimeControl.Web.Pages.Exceptions;

public sealed class DetailsModel(IExceptionRepository repository) : PageModel
{
    public DetectedException Exception { get; private set; } = null!;

    [BindProperty]
    public ReviewDecision Decision { get; set; }

    public async Task<IActionResult> OnGetAsync(
        int id,
        CancellationToken cancellationToken)
    {
        var detected = await repository.GetAsync(id, cancellationToken);
        if (detected is null)
        {
            return NotFound();
        }

        Exception = detected;
        Decision = detected.ReviewDecision;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(
        int id,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(Decision))
        {
            ModelState.AddModelError(nameof(Decision), "Ongeldige reviewactie.");
            return await OnGetAsync(id, cancellationToken);
        }

        await repository.UpdateReviewAsync(id, Decision, cancellationToken);
        return RedirectToPage(new { id });
    }
}
