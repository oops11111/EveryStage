namespace EveryStage.Terminal.Devices;

/// <summary>PLANNING.md §7 "信任策略": a paired device is either trusted (auto-accept future
/// requests) or still requires a manual confirmation every time.</summary>
public enum TrustMode { RequireManualConfirmation, Trusted }

/// <summary>
/// A remembered Caster (PLANNING.md §7). "允许被投放" and "允许被监看" are independent switches on
/// purpose — pairing alone grants neither ("权限分离...不因配对而默认互相授权"): a device can be
/// paired and trusted for casting without ever being allowed to view this Terminal's state, or the
/// other way around.
/// </summary>
public sealed class PairedDevice
{
    public required Guid DeviceId { get; set; }
    public required string DeviceName { get; set; }
    public TrustMode TrustMode { get; set; } = TrustMode.RequireManualConfirmation;
    public bool AllowCast { get; set; }
    public bool AllowMonitor { get; set; }
    public DateTimeOffset PairedAt { get; set; }
    /// <summary>Shared 256-bit key. Null only for records migrated from the pre-authentication
    /// format; those records must pair again before authenticated control messages are accepted.</summary>
    public string? PairingKey { get; set; }
}
