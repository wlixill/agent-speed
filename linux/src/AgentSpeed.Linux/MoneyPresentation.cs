using AgentSpeed.Core;
using AgentSpeed.Core.Money;
using AgentSpeed.Core.Network;
using AgentSpeed.Presentation.History;
using AgentSpeed.Presentation.Cost;

namespace AgentSpeed.Linux;

/// <summary>API-equivalent money; unknown prices or missing exchange rates never become a valid-looking zero.</summary>
public static class MoneyPresentation
{
    public static LiveMoney Readout(CostDisplay display, SpeedSnapshot snapshot, SourceSet? sources = null, bool apiEquivalent = false)
    {
        var billable = apiEquivalent ? SourceSet.Everything : display.Billable;
        var reading = OrbSubset.Cost(snapshot, sources, billable);
        var money = display.Live(reading, snapshot.Agents.Count(agent => sources?.Contains(agent.Source) ?? true));
        var invalid = snapshot.Sources.Where(source => !OrbSubset.IsUnavailable(source) && billable.Contains(source.Kind)
            && (sources?.Contains(source.Kind) ?? true)).Any(source =>
                !double.IsFinite(source.Live.Output) || source.Live.Output < 0
                || !double.IsFinite(source.LiveCost.MicroUsdPerSecond) || source.LiveCost.MicroUsdPerSecond < 0
                || !double.IsFinite(source.LiveCost.KnownOutput) || source.LiveCost.KnownOutput < 0);
        if (invalid || ConversionRate(display.Currency, display.Rates, display.Currencies) is null)
            return money with { Number = "—", Inline = "—", Unknown = true, Value = 0, HoverLine = "费用或所选币种汇率未知" };
        // A priced free portion does not establish that the unpriced remainder is free, even when the rounded sum is zero.
        if (reading.State != CostState.SubscriptionOnly && reading.LiveOutput > 0 && !money.Unknown
            && reading.KnownShare < CostReading.PartialThreshold)
            money = money with { AtLeast = true, HoverLine = "部分用量价格未知；显示已知费用的下限。" };
        return money;
    }

    public static string History(HistoryCost cost, bool hasData, Currency currency, RatesInUse rates, CurrencyTable? currencies = null)
    {
        if (hasData && cost.Kind == HistoryCostKind.Subscription) return "订阅（已排除）";
        if (!hasData || cost.MicroUsd < 0 || cost.Kind is HistoryCostKind.Unknown or HistoryCostKind.Unavailable
            || ConversionRate(currency, rates, currencies) is not { } rate) return "—";
        var value = MoneyFormat.Amount(cost.MicroUsd, rate);
        return (cost.IsPartial ? "≥" : "≈") + MoneyFormat.Format(value, currency, "zh-Hans", MoneyForm.Amount).Text;
    }

    public static string Live(LiveCost cost, double liveOutput, Currency currency, RatesInUse rates, CurrencyTable? currencies = null)
    {
        if (!double.IsFinite(liveOutput) || liveOutput < 0 || !double.IsFinite(cost.MicroUsdPerSecond)
            || cost.MicroUsdPerSecond < 0 || !double.IsFinite(cost.KnownOutput) || cost.KnownOutput < 0
            || liveOutput > 0 && cost.KnownOutput <= 0
            || ConversionRate(currency, rates, currencies) is not { } rate) return "—";
        var value = MoneyFormat.Rate(cost.MicroUsdPerSecond, rate);
        // A known free portion does not price the rest of the output: zero is still only a lower bound.
        var partial = cost.KnownShare(liveOutput) < CostReading.PartialThreshold;
        return (partial ? "≥" : "≈") + MoneyFormat.Format(value, currency, "zh-Hans", MoneyForm.Rate).Text;
    }

    /// <summary>Use the accepted rate or the documented bundled fallback; never invent a 1:1 conversion.</summary>
    public static decimal? ConversionRate(Currency currency, RatesInUse rates, CurrencyTable? currencies = null)
    {
        if (currency.Code == Currency.UsdCode) return 1;
        if (rates.Rates.TryGetValue(currency.Code, out var accepted) && accepted > 0) return accepted;
        var fallback = (currencies ?? CurrencyTable.Bundled).FallbackRates.Rates;
        return fallback.TryGetValue(currency.Code, out var bundled) && bundled > 0 ? bundled : null;
    }
}
