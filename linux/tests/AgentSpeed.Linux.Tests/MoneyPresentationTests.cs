using AgentSpeed.Core;
using AgentSpeed.Core.Money;
using AgentSpeed.Core.Network;
using AgentSpeed.Presentation.History;

namespace AgentSpeed.Linux.Tests;

public sealed class MoneyPresentationTests
{
    private static readonly Currency Usd = new("USD", 2, "US$", new Dictionary<string, string>());
    private static readonly Currency Cny = new("CNY", 2, "¥", new Dictionary<string, string>());
    private static readonly FallbackRates Fallback = new("2026-10-27", "fixture", new Dictionary<string, decimal> { ["USD"] = 1, ["CNY"] = 7.25m });
    private static readonly CurrencyTable Currencies = new([Usd, Cny], Fallback);
    private static readonly RatesInUse Rates = new(null, Fallback);

    [Fact]
    public void HistoryKeepsUnknownUnavailableAndNoDataSeparateFromAKnownZero()
    {
        Assert.Equal("—", MoneyPresentation.History(new HistoryCost(new CostTally(0, 2), 2), true, Usd, Rates, Currencies));
        Assert.Equal("—", MoneyPresentation.History(new HistoryCost(null, 2), true, Usd, Rates, Currencies));
        Assert.Equal("—", MoneyPresentation.History(new HistoryCost(CostTally.Zero, 0), false, Usd, Rates, Currencies));
        Assert.Equal("≈US$0.00", MoneyPresentation.History(new HistoryCost(CostTally.Zero, 1), true, Usd, Rates, Currencies));
    }

    [Fact]
    public void PartialCostsKeepTheirLowerBoundAndCurrencyConversionDoesNotChangeStoredUsd()
    {
        var partial = new HistoryCost(new CostTally(1_000_000, 1), 2);
        Assert.Equal("≥US$1.00", MoneyPresentation.History(partial, true, Usd, Rates, Currencies));
        Assert.Equal("≥¥7.25", MoneyPresentation.History(partial, true, Cny, Rates, Currencies));
        Assert.Equal(1_000_000, partial.MicroUsd);
    }

    [Fact]
    public void LiveMoneyTracksPriceCoverageAndUsesTheSameSelectedCurrency()
    {
        Assert.Equal("—", MoneyPresentation.Live(new LiveCost(0, 0), 100, Usd, Rates, Currencies));
        Assert.StartsWith("≥", MoneyPresentation.Live(new LiveCost(2500, 50), 100, Cny, Rates, Currencies));
        Assert.Equal("≈0.018 ¥/s", MoneyPresentation.Live(new LiveCost(2500, 100), 100, Cny, Rates, Currencies));
        Assert.Equal("≈0 US$/s", MoneyPresentation.Live(LiveCost.Zero, 0, Usd, Rates, Currencies));
    }

    [Fact]
    public void MissingExchangeRatesStayUnknownInsteadOfInventingAOneToOneConversion()
    {
        var unknown = new Currency("XXX", 2, "X", new Dictionary<string, string>());
        var history = new HistoryCost(new CostTally(1_000_000), 1);
        Assert.Null(MoneyPresentation.ConversionRate(unknown, Rates, Currencies));
        Assert.Equal("—", MoneyPresentation.History(history, true, unknown, Rates, Currencies));
        Assert.Equal("—", MoneyPresentation.Live(new LiveCost(1_000_000, 10), 10, unknown, Rates, Currencies));
    }

    [Fact]
    public void AFreePricedPortionDoesNotMakeTheUnknownRemainderLookFree()
    {
        Assert.Equal("≥0 US$/s", MoneyPresentation.Live(new LiveCost(0, 50), 100, Usd, Rates, Currencies));
        Assert.Equal("≈0 US$/s", MoneyPresentation.Live(new LiveCost(0, 100), 100, Usd, Rates, Currencies));
        Assert.Equal("—", MoneyPresentation.Live(new LiveCost(0, 0), 100, Usd, Rates, Currencies));
    }

    [Theory]
    [InlineData(double.NaN, 100, 100)]
    [InlineData(double.PositiveInfinity, 100, 100)]
    [InlineData(-1, 100, 100)]
    [InlineData(1, -1, 100)]
    [InlineData(1, 100, double.NaN)]
    public void InvalidLiveTelemetryNeverLooksLikeAKnownCost(double micro, double known, double output) =>
        Assert.Equal("—", MoneyPresentation.Live(new LiveCost(micro, known), output, Usd, Rates, Currencies));
}
