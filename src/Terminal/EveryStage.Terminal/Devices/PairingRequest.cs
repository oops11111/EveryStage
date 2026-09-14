namespace EveryStage.Terminal.Devices;

/// <summary>A pairing request awaiting the human confirmation PLANNING.md §7 calls for
/// ("首次需接收端确认（弹窗/PIN码）"). Hand this to whatever UI ends up showing that popup/PIN
/// prompt; resolve it by calling <see cref="DiscoveryService.RespondToPairing"/> with this
/// <see cref="RequestId"/>.</summary>
public sealed record PairingRequest(string RequestId, Guid DeviceId, string DeviceName, string RemoteAddress);
