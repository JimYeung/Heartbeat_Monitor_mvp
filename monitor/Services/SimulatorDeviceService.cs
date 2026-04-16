using System.Threading.Channels;
using Monitor.Models;

namespace Monitor.Services;

/// <summary>
/// Software ECG simulator — no hardware required.
///
/// Generates samples from the same 200-point lookup table used in the STM32
/// firmware, at 250 Hz, and writes them into a Channel just like TcpDeviceService.
/// Drop simulation is not applicable so DroppedPackets is always 0.
/// </summary>
public sealed class SimulatorDeviceService : IDeviceService
{
    // 200-sample ECG table at 250 Hz = 75 BPM (matches firmware exactly).
    private static readonly short[] EcgTable =
    [
        // 0-19: baseline
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        // 20-40: P wave
        20, 30, 42, 56, 73, 91, 109, 125, 138, 147,
        150, 147, 138, 125, 109, 91, 73, 56, 42, 30, 20,
        // 41-54: PQ segment
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        // 55-57: Q dip
        -80, -160, -200,
        // 58-62: R ascent
        -100, 300, 900, 1600, 2000,
        // 63-67: R descent / S
        1400, 600, -100, -450, -500,
        // 68-72: S recovery
        -350, -200, -100, 0, 30,
        // 73-99: ST segment
        50, 48, 46, 44, 42, 40, 38, 36, 34, 32,
        30, 28, 26, 24, 22, 20, 18, 16, 14, 12,
        10, 9, 8, 6, 5, 3, 2,
        // 100-159: T wave
        79,  89, 100, 111, 123, 137, 150, 164, 179, 195,
        210, 227, 243, 259, 275, 290, 306, 320, 334, 347,
        359, 369, 378, 386, 392, 396, 399, 400, 399, 396,
        392, 386, 378, 369, 359, 347, 334, 320, 306, 290,
        275, 259, 243, 227, 210, 195, 179, 164, 150, 137,
        123, 111, 100,  89,  79,  70,  62,  54,  47,  41,
        // 160-199: TP baseline
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
    ];

    private readonly Channel<Sample> _channel =
        Channel.CreateBounded<Sample>(new BoundedChannelOptions(2000)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });

    private CancellationTokenSource? _cts;
    private Task? _loop;

    public ChannelReader<Sample> Samples => _channel.Reader;
    public int DroppedPackets => 0;

    public Task ConnectAsync(CancellationToken ct)
    {
        _cts  = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loop = Task.Run(() => RunLoopAsync(_cts.Token));
        return Task.CompletedTask;
    }

    public void Disconnect()
    {
        _cts?.Cancel();
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_loop is not null)
            await _loop.ConfigureAwait(false);
        _cts?.Dispose();
    }

    // Produces one sample every 4 ms (250 Hz) using a high-resolution timer.
    private async Task RunLoopAsync(CancellationToken ct)
    {
        int    tableIdx = 0;
        ushort seq      = 0;

        // Target interval: 4 ms per sample (250 Hz).
        // Task.Delay(4) drifts slightly, so we track absolute next-fire time.
        var next = DateTime.UtcNow;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                next = next.AddMilliseconds(4);

                var sample = new Sample(DateTime.UtcNow, seq++, EcgTable[tableIdx]);
                tableIdx = (tableIdx + 1) % EcgTable.Length;

                await _channel.Writer.WriteAsync(sample, ct).ConfigureAwait(false);

                // Sleep until the next tick, clamping to 0 if we're already late.
                var delay = next - DateTime.UtcNow;
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _channel.Writer.TryComplete();
        }
    }
}
