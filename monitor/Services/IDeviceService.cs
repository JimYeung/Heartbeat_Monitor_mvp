using System.Threading.Channels;
using Monitor.Models;

namespace Monitor.Services;

/// <summary>
/// Abstraction over the data source — implemented by TcpDeviceService (simulator)
/// and SerialDeviceService (real Nucleo, Day 5).
/// </summary>
public interface IDeviceService : IAsyncDisposable
{
    /// <summary>Samples decoded from the packet stream. Readable from any thread.</summary>
    ChannelReader<Sample> Samples { get; }

    /// <summary>
    /// Opens the connection and starts the background read loop.
    /// Returns once connected; throws on failure.
    /// </summary>
    Task ConnectAsync(CancellationToken ct);

    /// <summary>Cancels the read loop and closes the connection.</summary>
    void Disconnect();

    /// <summary>Running count of packets dropped due to sequence gaps (SR-08).</summary>
    int DroppedPackets { get; }
}
