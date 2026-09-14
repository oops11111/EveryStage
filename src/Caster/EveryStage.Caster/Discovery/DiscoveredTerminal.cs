using System.Net;

namespace EveryStage.Caster.Discovery;

/// <summary>One Terminal currently visible on the LAN (PLANNING.md §12: "待机态：目标终端机列表"),
/// built purely from beacon traffic — see <see cref="TerminalDiscoveryClient"/>.</summary>
public sealed record DiscoveredTerminal(Guid DeviceId, string DeviceName, IPAddress Address, DateTimeOffset LastSeenAt);
