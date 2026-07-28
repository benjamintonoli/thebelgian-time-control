using TheBelgian.TimeControl.Infrastructure.Powerfleet;

namespace TheBelgian.TimeControl.Tests;

public sealed class PowerfleetXmlParserTests
{
    private readonly PowerfleetXmlParser _parser = new();

    [Fact]
    public void Parse_ReadsLocalExampleWithoutHttp()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Samples", "powerfleet-report.xml");
        var xml = File.ReadAllText(path);

        var trip = Assert.Single(_parser.Parse(xml));

        Assert.Equal("trip-1001", trip.ExternalId);
        Assert.Equal("tech-42", trip.DriverId);
        Assert.Equal(37, trip.DurationMinutes);
        Assert.Equal(24.5m, trip.DistanceKilometres);
    }

    [Fact]
    public void Parse_ReportsMalformedXmlClearly()
    {
        var exception = Assert.Throws<InvalidDataException>(() => _parser.Parse("<report>"));

        Assert.Contains("ongeldig", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("regel", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_RejectsXmlWithoutTrips()
    {
        var exception = Assert.Throws<InvalidDataException>(() => _parser.Parse("<report />"));

        Assert.Contains("geen herkenbare ritrecords", exception.Message);
    }
}
