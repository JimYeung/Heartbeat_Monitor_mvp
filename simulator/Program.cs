// ECG Simulator — impersonates the Nucleo firmware over TCP.
// Sends 8-byte packets conforming to PROTOCOL.md at 250 Hz.
//
// Usage: simulator [port]
//   port  TCP port to listen on (default: 5000)
//
// The WPF monitor connects to localhost:<port> instead of a COM port.
// This avoids the need for com0com virtual serial port drivers.

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

await Simulator.RunAsync(args);

internal static class Simulator
{
    // One cardiac cycle at ~75 BPM sampled at 250 Hz = 200 samples.
    // 1 heartbeat = 200 samples. 
    //
    // Anatomy:
    //   [0-7]    TP baseline     flat
    //   [8-27]   P wave          atrial depolarisation, gentle bump ~310
    //   [28-42]  PR segment      flat
    //   [43-45]  Q wave          small negative deflection
    //   [46-52]  R wave          dominant spike ~2500
    //   [53-58]  S wave          small negative after R
    //   [59-75]  ST segment      return to baseline
    //   [76-115] T wave          ventricular repolarisation, broad bump ~450
    //   [116-199] TP baseline    flat (diastole rest period)
    private static readonly short[] EcgTable =
    [
        // --- TP baseline (0-7) ---
        0, 0, 0, 0, 0, 0, 0, 0,

        // --- P wave (8-27, 20 samples, peak ~314) ---
        8, 25, 55, 95, 145, 200, 248, 285, 308, 314,
        312, 295, 265, 225, 178, 130, 85, 48, 20, 5,

        // --- PR segment (28-42, 15 samples) ---
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,

        // --- QRS complex (43-58, 16 samples) ---
        // Q dip
        -40, -110, -130,
        // R wave
        350, 1300, 2200, 2500, 2100, 1100, 150,
        // S wave
        -120, -290, -360, -260, -110, -15,

        // --- ST segment (59-75, 17 samples) ---
        5, 8, 6, 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,

        // --- T wave (76-115, 40 samples, peak ~448) ---
        4, 14, 32, 60, 97, 140, 188, 238, 285, 328,
        363, 393, 415, 432, 443, 448, 447, 440, 427, 409,
        385, 357, 325, 290, 254, 218, 183, 150, 120, 94,
        71, 53, 38, 27, 18, 12, 7, 4, 2, 0,

        // --- TP baseline (116-199, 84 samples) ---
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0,
    ];

    public static async Task RunAsync(string[] args)
    {
        int port = args.Length > 0 && int.TryParse(args[0], out int p) ? p : 5000;

        Console.WriteLine("ECG Simulator — PROTOCOL.md v1.0");
        Console.WriteLine($"TCP port   : {port}");
        Console.WriteLine($"Sample rate: 250 Hz  |  Packet: 8 bytes  |  Waveform: ~75 BPM");
        Console.WriteLine("Press Ctrl+C to exit.");
        Console.WriteLine();

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        Console.WriteLine($"Listening on localhost:{port} — waiting for monitor...");

        while (!cts.Token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Monitor connected — streaming ECG...");
            await StreamEcgAsync(client, cts.Token);
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Monitor disconnected.");
            Console.WriteLine();
        }

        listener.Stop();
        Console.WriteLine("Simulator stopped.");
    }

    private static async Task StreamEcgAsync(TcpClient client, CancellationToken ct)
    {
        using var _ = client;
        await using var stream = client.GetStream();

        ushort seq = 0;
        int tableIndex = 0;
        long packetCount = 0;

        // Accurate 4 ms (250 Hz) pacing: Task.Delay for most of the wait,
        // then spin the last ~1 ms for fine-grained accuracy.
        const long TicksPerSample = TimeSpan.TicksPerMillisecond * 4;
        var sw = Stopwatch.StartNew();
        long nextTick = sw.ElapsedTicks;

        byte[] packet = new byte[8]; // reused every iteration

        try
        {
            while (!ct.IsCancellationRequested)
            {
                long waitTicks = nextTick - sw.ElapsedTicks - TimeSpan.TicksPerMillisecond;
                if (waitTicks > 0)
                    await Task.Delay(TimeSpan.FromTicks(waitTicks), ct);

                while (sw.ElapsedTicks < nextTick) { }
                nextTick += TicksPerSample;

                BuildPacket(packet, seq, EcgTable[tableIndex]);
                seq = unchecked((ushort)(seq + 1));
                tableIndex = (tableIndex + 1) % EcgTable.Length;

                await stream.WriteAsync(packet, ct);

                packetCount++;
                if (packetCount % 250 == 0)
                    Console.Write($"\r  {packetCount,8} packets sent  ({packetCount / 250} s)   ");
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
        {
            // Normal: client disconnected or shutdown requested.
        }
        finally
        {
            Console.WriteLine();
        }
    }

    // Builds one 8-byte packet in-place. See PROTOCOL.md for field layout.
    private static void BuildPacket(byte[] buf, ushort seq, short sample)
    {
        // SOF: start of the frame marker
        buf[0] = 0xAA;
        buf[1] = 0x55;

        // splitting 16-bit short seq into 2 bytes with little endian fashion
        buf[2] = (byte)(seq & 0xFF);
        buf[3] = (byte)(seq >> 8);

        // same splitting and shifting with the raw sample. 
        ushort raw = (ushort)sample;
        buf[4] = (byte)(raw & 0xFF);
        buf[5] = (byte)(raw >> 8);

        // circulant redundancy check (CRC) for error detection, calculated over the seq and sample fields (bytes 2-5).
        ushort crc = Crc16Ccitt(buf, offset: 2, length: 4);
        buf[6] = (byte)(crc & 0xFF);
        buf[7] = (byte)(crc >> 8);
    }

    // CRC-16 CCITT — matches PROTOCOL.md reference implementation exactly.
    private static ushort Crc16Ccitt(byte[] data, int offset, int length)
    {
        ushort crc = 0xFFFF;
        for (int i = offset; i < offset + length; i++)
        {
            crc ^= (ushort)(data[i] << 8);
            for (int bit = 0; bit < 8; bit++)
                crc = (crc & 0x8000) != 0
                    ? (ushort)((crc << 1) ^ 0x1021)
                    : (ushort)(crc << 1);
        }
        return crc;
    }
}
