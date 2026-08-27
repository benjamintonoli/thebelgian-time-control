using System.Globalization;
using System.Text;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Core.Services;

namespace TheBelgian.TimeControl.Infrastructure.AdminReview;

internal static class DailyFactualReportBuilder
{
    private static readonly CultureInfo Dutch = CultureInfo.GetCultureInfo("nl-BE");

    public static string Build(
        IReadOnlyList<DailyReviewCase> cases,
        string generatedBy,
        DateTimeOffset generatedAt)
    {
        if (cases.Count == 0)
        {
            throw new InvalidOperationException("Selecteer minimaal één beoordeelde case.");
        }

        var technician = cases[0].Technician;
        if (cases.Any(item => !string.Equals(
                item.Technician,
                technician,
                StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                "Een gecombineerd feitenrapport kan alleen cases van dezelfde technieker bevatten.");
        }

        var builder = new StringBuilder();
        builder.AppendLine("TIME CONTROL — FEITENRAPPORT");
        builder.AppendLine();
        builder.AppendLine(Dutch, $"Technieker: {technician}");
        builder.AppendLine(Dutch, $"Gegenereerd: {generatedAt.ToLocalTime():dd/MM/yyyy HH:mm}");
        builder.AppendLine(Dutch, $"Geselecteerd door: {generatedBy}");
        builder.AppendLine();
        builder.AppendLine(
            "Dit rapport bundelt uitsluitend de door een admin geselecteerde tijds- en locatiefeiten. " +
            "Het bevat geen juridische of personeelsrechtelijke conclusie.");

        foreach (var item in cases.OrderBy(value => value.Date))
        {
            builder.AppendLine();
            builder.AppendLine(Dutch, $"{item.Date:dd MMMM yyyy} — {item.Customer}");
            builder.AppendLine(Dutch, $"Adres: {item.Address}");
            AppendStart(builder, item.First);
            AppendEnd(builder, item.Last);
            builder.AppendLine(Dutch, $"Adminbeslissing: {DailyReviewDisplay.Status(item.Decision.Status)}");
            if (item.Decision.Reason is { } reason)
            {
                builder.AppendLine(Dutch, $"Reden: {DailyReviewDisplay.Reason(reason)}");
            }

            if (!string.IsNullOrWhiteSpace(item.Decision.Notes))
            {
                builder.AppendLine(Dutch, $"Adminnotitie: {item.Decision.Notes}");
            }
        }

        return builder.ToString();
    }

    private static void AppendStart(StringBuilder builder, DailyReviewBoundaryEvidence boundary)
    {
        builder.AppendLine(Dutch, $"Geregistreerde start eerste klantprestatie: {boundary.PlenionTime:HH:mm}");
        builder.AppendLine(boundary.GpsTime is { } gps
            ? $"GPS-aankomst op werklocatie: {gps:HH:mm:ss}"
            : "GPS-aankomst op werklocatie: onvoldoende locatiegegevens");
        builder.AppendLine(DailyReviewDisplay.Difference(boundary));
    }

    private static void AppendEnd(StringBuilder builder, DailyReviewBoundaryEvidence boundary)
    {
        builder.AppendLine(boundary.GpsTime is { } gps
            ? $"GPS-vertrek van werklocatie: {gps:HH:mm:ss}"
            : "GPS-vertrek van werklocatie: onvoldoende locatiegegevens");
        builder.AppendLine(Dutch, $"Geregistreerd einde laatste klantprestatie: {boundary.PlenionTime:HH:mm}");
        builder.AppendLine(DailyReviewDisplay.Difference(boundary));
    }
}
