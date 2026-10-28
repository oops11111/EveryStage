using System.Net;
using EveryStage.Discovery;

namespace EveryStage.Terminal.Devices;

internal interface IDeviceCastTransport
{
    Task SendCastStatusAsync(IPEndPoint casterEndPoint, DiscoveryProtocol.CastStatusMessage status);
    Task SendCastNackAsync(IPEndPoint casterEndPoint, Guid mediaSessionId, IReadOnlyList<ushort> missingSequences);
    Task<TimeSpan?> PingAsync(IPEndPoint casterEndPoint, TimeSpan? timeout = null);
}
