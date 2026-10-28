using System.Text.Json;
using System.Text.Json.Serialization;
using AgentSpeed.Core;
using AgentSpeed.Presentation.Settings;

namespace AgentSpeed.Linux;

/// <summary>SourceSet has no JSON constructor; persist explicit enum members rather than losing subscription marks.</summary>
public sealed class LinuxCostSettingsConverter : JsonConverter<CostSettings>
{
    private sealed record Stored(string Currency = "USD", LiveUnit LiveUnit = LiveUnit.Tokens,
        SourceKind[]? Subscriptions = null, bool IncludeSubscriptions = false, bool SyncPrices = false,
        bool? HistoryIncludeSubscriptions = null);

    public override CostSettings Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        var value = JsonSerializer.Deserialize<Stored>(ref reader, options) ?? new();
        return new CostSettings
        {
            Currency = value.Currency, LiveUnit = value.LiveUnit,
            Subscriptions = SourceSet.From((value.Subscriptions ?? []).Where(Enum.IsDefined)),
            IncludeSubscriptions = value.IncludeSubscriptions, SyncPrices = value.SyncPrices,
            HistoryIncludeSubscriptions = value.HistoryIncludeSubscriptions,
        }.Validated();
    }

    public override void Write(Utf8JsonWriter writer, CostSettings value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, new Stored(value.Currency, value.LiveUnit, value.Subscriptions.ToArray(),
            value.IncludeSubscriptions, value.SyncPrices, value.HistoryIncludeSubscriptions), options);
}
