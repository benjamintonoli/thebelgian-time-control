namespace TheBelgian.TimeControl.Core.Models;

public static class DisplayNames
{
    public static string ToDisplayName(this ExceptionType value) => value switch
    {
        ExceptionType.None => "Geen afwijking",
        ExceptionType.RegisteredStartTooEarly => "Te vroege geregistreerde start",
        ExceptionType.RegisteredEndTooLate => "Te late geregistreerde eindtijd",
        ExceptionType.RegisteredTravelExceedsPowerfleet =>
            "Geregistreerde verplaatsing hoger dan Powerfleet-rijtijd",
        ExceptionType.StructuralPattern => "Structureel patroon",
        ExceptionType.InsufficientPowerfleetData => "Onvoldoende Powerfleet-data",
        ExceptionType.UncertainVehicleAssignment => "Onzekere voertuigtoewijzing",
        ExceptionType.ManualReviewRequired => "Manuele controle nodig",
        _ => value.ToString(),
    };

    public static string ToDisplayName(this ExceptionPriority value) => value switch
    {
        ExceptionPriority.Low => "Laag",
        ExceptionPriority.Normal => "Normaal",
        ExceptionPriority.High => "Hoog",
        _ => value.ToString(),
    };

    public static string ToDisplayName(this ReviewDecision value) => value switch
    {
        ReviewDecision.Unreviewed => "Niet beoordeeld",
        ReviewDecision.CorrectRegistration => "Correcte registratie",
        ReviewDecision.ManualReviewRequired => "Manuele controle nodig",
        ReviewDecision.InsufficientGpsData => "Onvoldoende GPS-data",
        ReviewDecision.VehicleChange => "Autowissel",
        ReviewDecision.ExceptionConfirmed => "Afwijking bevestigd",
        _ => value.ToString(),
    };
}
