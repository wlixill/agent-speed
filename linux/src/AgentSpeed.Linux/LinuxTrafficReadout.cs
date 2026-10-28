using AgentSpeed.Core;
using AgentSpeed.Core.Traffic;
using AgentSpeed.Presentation.Settings.Orbs;

namespace AgentSpeed.Linux;

/// <summary>Machine counters and process application-byte observations have different scopes; label them on every surface.</summary>
public static class LinuxTrafficReadout
{
    public static string Describe(TrafficSnapshot snapshot, SourceSet? sources, OrbTraffic mode, bool bits, bool includeTools)
    {
        if (!snapshot.Enabled || mode == OrbTraffic.Off) return "";
        var rate = Rate(snapshot, sources, mode, includeTools);
        var prefix = mode == OrbTraffic.Machine ? "网卡 " : "进程观测 ";
        return prefix + "↓ " + Format(rate?.Rx, bits) + "\n" + prefix + "↑ " + Format(rate?.Tx, bits);
    }

    public static TrafficRate? Rate(TrafficSnapshot snapshot, SourceSet? sources, OrbTraffic mode, bool includeTools) =>
        !snapshot.Enabled ? null : mode switch
        {
            OrbTraffic.Machine => snapshot.MachineLive,
            OrbTraffic.Sources => snapshot.ProcessState.Kind is ProcessTrafficKind.Running or ProcessTrafficKind.Standby
                ? snapshot.Live(sources, includeTools) : null,
            _ => null,
        };
    private static string Format(double? value, bool bits) => value is { } number && double.IsFinite(number) && number >= 0
        ? TrafficFormat.Rate(number, bits) : "—";
}
