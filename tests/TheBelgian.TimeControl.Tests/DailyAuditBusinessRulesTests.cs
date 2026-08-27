using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Core.Services;
using TheBelgian.TimeControl.Infrastructure.Pilot;

namespace TheBelgian.TimeControl.Tests;

public sealed class DailyAuditBusinessRulesTests
{
    [Theory]
    [InlineData(2026, 7, 21, "Nationale feestdag")]
    [InlineData(2026, 4, 6, "Paasmaandag")]
    [InlineData(2026, 5, 14, "Onze-Lieve-Heer-Hemelvaart")]
    [InlineData(2026, 5, 25, "Pinkstermaandag")]
    [InlineData(2027, 3, 29, "Paasmaandag")]
    public void BelgianHolidayCalendar_IncludesFixedAndMovingHolidays(
        int year,
        int month,
        int day,
        string expected)
    {
        Assert.Equal(expected, BelgianPublicHolidayCalendar.GetHolidayName(new DateOnly(year, month, day)));
    }

    [Fact]
    public void DayEligibility_ExcludesPublicHolidayBeforeAnyPerformanceAnalysis()
    {
        var result = DailyAuditDayEligibility.Evaluate(new DateOnly(2026, 7, 21), []);

        Assert.Equal(DailyAuditDayStatus.ExcludedPublicHoliday, result.Status);
        Assert.False(result.IsEligible);
    }

    [Fact]
    public void DayEligibility_FullPlannedLeaveWinsIndependentlyOfRegisteredPerformances()
    {
        var date = new DateOnly(2026, 7, 10);
        var result = DailyAuditDayEligibility.Evaluate(
            date,
            [new DailyAbsenceWindow(At(date, 7), At(date, 17), PlenionCalendarAbsenceKind.Leave, "verlof")]);

        Assert.Equal(DailyAuditDayStatus.ExcludedLeave, result.Status);
    }

    [Fact]
    public void DayEligibility_PartialLeave_DoesNotExcludeWholeDay()
    {
        var date = new DateOnly(2026, 7, 24);
        var result = DailyAuditDayEligibility.Evaluate(
            date,
            [new DailyAbsenceWindow(At(date, 8), At(date, 12), PlenionCalendarAbsenceKind.Leave, "verlof")]);

        Assert.Equal(DailyAuditDayStatus.Eligible, result.Status);
    }

    [Fact]
    public void TravelRule_Joris29July_IsValidTravelFromTheBelgian()
    {
        var date = new DateOnly(2026, 7, 29);
        var travel = Performance(280570, date, 7, 25, 7, 40, "5", "Onderhoud 26500697");
        var customer = Performance(280569, date, 7, 40, 11, 50, "9", "Onderhoud 26500697");
        var company = Stop(
            "297478793/297491946",
            date,
            At(date, 6, 36),
            At(date, 7, 2),
            50.98431m,
            4.30087m,
            "Slozenstraat 86, 1861 Meise");
        var official = Stop(
            "297491946/297557846",
            date,
            At(date, 7, 40),
            At(date, 11, 22),
            50.866m,
            4.087m,
            "Ed. Schelfhoutstraat 232, Liedekerke");

        var result = DailyTravelRuleEvaluator.EvaluateFirstBoundary(
            [travel, customer],
            [company, official],
            customer,
            official.Arrival,
            new HaversineDistanceCalculator());

        Assert.True(result.IsValid);
        Assert.Equal(DailyTravelRuleStatus.ValidTravelFromTheBelgian, result.Status);
        Assert.Equal(280570, result.TravelPerformanceId);
        Assert.Equal(5, result.ToleranceMinutes);
        Assert.Equal(23, result.StartDifferenceMinutes);
        Assert.Equal(0, result.EndDifferenceMinutes);
    }

    [Fact]
    public void TravelRule_DoesNotAcceptAnUnrelatedPreCustomerStop()
    {
        var date = new DateOnly(2026, 7, 29);
        var travel = Performance(1, date, 7, 25, 7, 40, "5", "Onderhoud");
        var customer = Performance(2, date, 7, 40, 11, 50, "9", "Onderhoud");
        var unrelated = Stop("a/b", date, At(date, 7), At(date, 7, 10), 50.8m, 4.1m, "Andere locatie");

        var result = DailyTravelRuleEvaluator.EvaluateFirstBoundary(
            [travel, customer],
            [unrelated],
            customer,
            At(date, 7, 40),
            new HaversineDistanceCalculator());

        Assert.False(result.IsValid);
        Assert.True(result.NeedsReview);
        Assert.Equal(DailyTravelRuleStatus.TravelNeedsReview, result.Status);
    }

    [Fact]
    public void TravelRule_MorningRejectsStartMoreThanFiveMinutesBeforeCompanyDeparture()
    {
        var date = new DateOnly(2026, 7, 1);
        var travel = Performance(3, date, 6, 54, 7, 30, "5", "Verplaatsing");
        var customer = Performance(4, date, 7, 40, 12, 0, "9", "Klantwerk");
        var company = Stop("company/customer", date, At(date, 6, 30), At(date, 7),
            50.98431m, 4.30087m, "Slozenstraat 86");

        var result = DailyTravelRuleEvaluator.EvaluateFirstBoundary(
            [travel, customer], [company], customer, At(date, 7, 40), new HaversineDistanceCalculator());

        Assert.True(result.NeedsReview);
        Assert.Equal(-6, result.StartDifferenceMinutes);
        Assert.Equal(-10, result.EndDifferenceMinutes);
    }

    [Fact]
    public void TravelRule_MorningAllowsLaterStartAndEarlierEnd()
    {
        var date = new DateOnly(2026, 7, 1);
        var travel = Performance(5, date, 7, 10, 7, 30, "5", "Verplaatsing");
        var customer = Performance(6, date, 7, 40, 12, 0, "9", "Klantwerk");
        var company = Stop("company/customer", date, At(date, 6, 30), At(date, 7),
            50.98431m, 4.30087m, "Slozenstraat 86");

        var result = DailyTravelRuleEvaluator.EvaluateFirstBoundary(
            [travel, customer], [company], customer, At(date, 7, 40), new HaversineDistanceCalculator());

        Assert.True(result.IsValid);
        Assert.Equal(10, result.StartDifferenceMinutes);
        Assert.Equal(-10, result.EndDifferenceMinutes);
    }

    [Fact]
    public void TravelRule_EveningToTheBelgian_IsValidTravelToTheBelgian()
    {
        var date = new DateOnly(2026, 7, 29);
        var customer = Performance(10, date, 13, 0, 16, 0, "9", "Service Interventie");
        var travel = Performance(11, date, 16, 0, 16, 35, "5", "Service Interventie");
        var customerStop = Stop(
            "customer/company",
            date,
            At(date, 13),
            At(date, 16, 2),
            50.866m,
            4.087m,
            "Klant");
        var companyStop = Stop(
            "company/home",
            date,
            At(date, 16, 35),
            At(date, 16, 50),
            50.98431m,
            4.30087m,
            "Slozenstraat 86, 1861 Meise");

        var result = DailyTravelRuleEvaluator.EvaluateLastBoundary(
            [customer, travel],
            [customerStop, companyStop],
            customer,
            customerStop.Departure,
            new HaversineDistanceCalculator());

        Assert.True(result.IsValid);
        Assert.Equal(DailyTravelRuleStatus.ValidTravelToTheBelgian, result.Status);
        Assert.Equal(11, result.TravelPerformanceId);
        Assert.Equal(-2, result.StartDifferenceMinutes);
        Assert.Equal(0, result.EndDifferenceMinutes);
    }

    [Fact]
    public void TravelRule_EveningRejectsEndMoreThanFiveMinutesAfterCompanyArrival()
    {
        var date = new DateOnly(2026, 7, 1);
        var customer = Performance(12, date, 13, 0, 16, 0, "9", "Klantwerk");
        var travel = Performance(13, date, 16, 0, 16, 41, "5", "Verplaatsing");
        var customerStop = Stop("customer/company", date, At(date, 13), At(date, 16),
            50.866m, 4.087m, "Klant");
        var company = Stop("company/home", date, At(date, 16, 35), At(date, 17),
            50.98431m, 4.30087m, "Slozenstraat 86");

        var result = DailyTravelRuleEvaluator.EvaluateLastBoundary(
            [customer, travel], [customerStop, company], customer, customerStop.Departure,
            new HaversineDistanceCalculator());

        Assert.True(result.NeedsReview);
        Assert.Equal(0, result.StartDifferenceMinutes);
        Assert.Equal(6, result.EndDifferenceMinutes);
    }

    [Fact]
    public void TravelRule_EveningKeepsExistingFifteenMinuteCandidateButMarksItForReview()
    {
        var date = new DateOnly(2026, 7, 1);
        var customer = Performance(16, date, 13, 0, 16, 0, "9", "Klantwerk");
        var travel = Performance(17, date, 15, 45, 16, 20, "5", "Verplaatsing");
        var customerStop = Stop("customer/company", date, At(date, 13), At(date, 16),
            50.866m, 4.087m, "Klant");
        var company = Stop("company/home", date, At(date, 16, 35), At(date, 17),
            50.98431m, 4.30087m, "Slozenstraat 86");

        var result = DailyTravelRuleEvaluator.EvaluateLastBoundary(
            [customer, travel], [customerStop, company], customer, customerStop.Departure,
            new HaversineDistanceCalculator());

        Assert.True(result.NeedsReview);
        Assert.Equal(17, result.TravelPerformanceId);
        Assert.Equal(-15, result.StartDifferenceMinutes);
    }

    [Fact]
    public void TravelRule_EveningAllowsLaterStartAndEarlierEnd()
    {
        var date = new DateOnly(2026, 7, 1);
        var customer = Performance(14, date, 13, 0, 16, 0, "9", "Klantwerk");
        var travel = Performance(15, date, 16, 10, 16, 30, "5", "Verplaatsing");
        var customerStop = Stop("customer/company", date, At(date, 13), At(date, 16),
            50.866m, 4.087m, "Klant");
        var company = Stop("company/home", date, At(date, 16, 35), At(date, 17),
            50.98431m, 4.30087m, "Slozenstraat 86");

        var result = DailyTravelRuleEvaluator.EvaluateLastBoundary(
            [customer, travel], [customerStop, company], customer, customerStop.Departure,
            new HaversineDistanceCalculator());

        Assert.True(result.IsValid);
        Assert.Equal(10, result.StartDifferenceMinutes);
        Assert.Equal(-5, result.EndDifferenceMinutes);
    }

    [Fact]
    public void TravelRule_EveningWithoutTheBelgian_IsTravelNeedsReview()
    {
        var date = new DateOnly(2026, 7, 29);
        var customer = Performance(20, date, 13, 0, 16, 0, "9", "Service Interventie");
        var travel = Performance(21, date, 16, 0, 16, 35, "5", "Service Interventie");
        var customerStop = Stop(
            "customer/home",
            date,
            At(date, 13),
            At(date, 16, 2),
            50.866m,
            4.087m,
            "Klant");
        var homeStop = Stop(
            "home/end",
            date,
            At(date, 16, 35),
            At(date, 17),
            50.7m,
            4.0m,
            "Elders");

        var result = DailyTravelRuleEvaluator.EvaluateLastBoundary(
            [customer, travel],
            [customerStop, homeStop],
            customer,
            customerStop.Departure,
            new HaversineDistanceCalculator());

        Assert.False(result.IsValid);
        Assert.True(result.NeedsReview);
        Assert.Equal(DailyTravelRuleStatus.TravelNeedsReview, result.Status);
    }

    private static NormalizedPilotPerformance Performance(
        long id,
        DateOnly date,
        int startHour,
        int startMinute,
        int endHour,
        int endMinute,
        string mainTask,
        string description) =>
        new(
            id, "resource", date, At(date, startHour, startMinute), At(date, endHour, endMinute),
            0, (int)(At(date, endHour, endMinute) - At(date, startHour, startMinute)).TotalMinutes,
            (int)(At(date, endHour, endMinute) - At(date, startHour, startMinute)).TotalMinutes,
            0, "project", mainTask, "bon", description, null, "project", "Project", "location",
            "Customer", "Street", "1000", "City", "BE", 1, 1, 1, "ok", "ok");

    private static PilotStop Stop(
        string id,
        DateOnly date,
        DateTimeOffset arrival,
        DateTimeOffset departure,
        decimal latitude,
        decimal longitude,
        string address) =>
        new(
            id, date, "incoming", "outgoing", arrival, departure,
            (int)(departure - arrival).TotalMinutes, address, null, null, null, null, null,
            latitude, longitude, null, "driver", "Driver", true, "ok");

    private static DateTimeOffset At(DateOnly date, int hour, int minute = 0) =>
        new(date.Year, date.Month, date.Day, hour, minute, 0, TimeSpan.FromHours(2));
}
