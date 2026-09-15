namespace EveryStage.Caster.Discovery;

/// <summary>
/// A Terminal this Caster has successfully paired with before (PLANNING.md §12 "已配对直显"),
/// persisted so it can still show up in the standby list even when it isn't currently broadcasting a
/// beacon — unlike <see cref="DiscoveredTerminal"/>, which only exists for as long as beacons keep
/// arriving. Deliberately much smaller than Terminal's own <c>Devices.PairedDevice</c>: there is no
/// trust mode or cast/monitor permission split here, because those are Terminal-side concepts (the
/// Terminal decides whether to accept a cast, not the Caster) — this record exists purely so the
/// Caster can remember "I've successfully paired with this device before" and show it, not to gate
/// any behavior on it.
///
/// Deliberately does NOT persist an IP address: a paired Terminal's address can change (DHCP lease
/// renewal, the machine moving to a different network) between sessions, and a stale cached address
/// would be actively misleading — silently attempting to cast to a "known" address that used to
/// belong to this Terminal is worse than requiring a fresh beacon to confirm it's actually still
/// there at that address. A paired entry with no matching live <see cref="DiscoveredTerminal"/> is
/// shown as offline and cannot be selected to start a cast — see <see cref="UI.MainForm"/>'s doc
/// comment on how the two lists are merged for display.
/// </summary>
public sealed record PairedTerminal(Guid DeviceId, string DeviceName, DateTimeOffset PairedAt);
