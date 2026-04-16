using System.IO;
using System.Net.Sockets;
using System.Threading.Channels;
using Monitor.Models;

namespace Monitor.Services;

/// <summary>
/// Connects to the ECG simulator over TCP (localhost:5000 by default).
/// Reads the raw byte stream, feeds it through PacketParser, and writes
/// decoded Samples into an unbounded Channel for the ViewModel to consume.
///
/// Replaces SerialDeviceService for the simulator phase (Days 1–3).
/// On Day 5, swap this for SerialDeviceService when the Nucleo is available.
/// </summary>
public sealed class TcpDeviceService : IDeviceService
{
    private readonly string _host;
    private readonly int _port;

    private readonly Channel<Sample> _channel = Channel.CreateUnbounded<Sample>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    private readonly PacketParser _parser = new();
    private TcpClient? _client;
    private CancellationTokenSource? _cts;
    private Task? _readTask;

    public ChannelReader<Sample> Samples => _channel.Reader;
    public int DroppedPackets => _parser.DroppedPackets;

    public TcpDeviceService(string host, int port)
    {
        _host = host;
        _port = port; 
    }

    public async Task ConnectAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        _client = new TcpClient();
        // This throws if the connection is refused — caller handles it.
        await _client.ConnectAsync(_host, _port, _cts.Token);

        // Start reading in background; do not await here.
        _readTask = ReadLoopAsync(_client, _cts.Token);
    }

    public void Disconnect()
    {
        _cts?.Cancel();
        _client?.Close(); // unblocks any pending ReadAsync
    }

    private async Task ReadLoopAsync(TcpClient client, CancellationToken ct)
    {
        await using var stream = client.GetStream();
        var buf = new byte[512]; // read in chunks for efficiency

        try
        {
            while (!ct.IsCancellationRequested)
            {
                int read = await stream.ReadAsync(buf, ct);
                if (read == 0) break; // graceful server close

                for (int i = 0; i < read; i++)
                {
                    var sample = _parser.Feed(buf[i]);
                    if (sample is not null)
                        await _channel.Writer.WriteAsync(sample, ct);
                }
            }
        }
        catch (Exception ex) when (
            ex is OperationCanceledException or
                  IOException or
                  SocketException)
        {
            // Normal shutdown paths — not an error.
        }
        finally
        {
            // Signal the channel consumer that no more data is coming.
            _channel.Writer.TryComplete();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Disconnect();
        if (_readTask is not null)
            await _readTask;
        _client?.Dispose();
        _cts?.Dispose();
    }
}