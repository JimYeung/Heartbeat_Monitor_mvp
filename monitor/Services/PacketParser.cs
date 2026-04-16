using Monitor.Models;

namespace Monitor.Services;

/// <summary>
/// Stateful byte-by-byte parser implementing the PROTOCOL.md state machine.
///
/// Call Feed(byte) for every byte received from the stream.
/// It returns a Sample when a complete, valid packet is decoded, or null otherwise.
///
/// State machine:
///   WAIT_SOF1 → (0xAA) → WAIT_SOF2
///   WAIT_SOF2 → (0x55) → READ_PAYLOAD  |  (0xAA) → stay  |  other → WAIT_SOF1
///   READ_PAYLOAD (6 bytes: seq + sample + crc) → verify CRC → emit Sample or drop
/// </summary>
public sealed class PacketParser
{
    private enum State { WaitSof1, WaitSof2, ReadPayload }

    private State _state = State.WaitSof1;
    private readonly byte[] _payload = new byte[6]; // seq(2) + sample(2) + crc(2)
    private int _payloadIndex;

    private ushort _lastSeq;
    private bool _seqInitialized;

    public int DroppedPackets { get; private set; }
    public int CrcErrors { get; private set; }

    public Sample? Feed(byte b)
    {
        switch (_state)
        {
            case State.WaitSof1:
                if (b == 0xAA) _state = State.WaitSof2;
                return null;

            case State.WaitSof2:
                if (b == 0x55)
                {
                    _payloadIndex = 0;
                    _state = State.ReadPayload;
                }
                else if (b != 0xAA) // 0xAA stays in WaitSof2 (could be real SOF1)
                {
                    _state = State.WaitSof1;
                }
                return null;

            case State.ReadPayload:
                _payload[_payloadIndex++] = b;
                if (_payloadIndex < 6) return null;

                _state = State.WaitSof1;
                return TryDecodePacket();
        }

        return null;
    }

    private Sample? TryDecodePacket()
    {
        ushort seq    = (ushort)(_payload[0] | (_payload[1] << 8));
        short  value  = (short) (_payload[2] | (_payload[3] << 8));
        ushort rxCrc  = (ushort)(_payload[4] | (_payload[5] << 8));
        ushort okCrc  = Crc16Ccitt(_payload, offset: 0, length: 4);

        if (rxCrc != okCrc)
        {
            CrcErrors++;
            return null; // discard — PROTOCOL.md §Error Handling
        }

        // Detect dropped packets via sequence gap (SR-08).
        if (_seqInitialized)
        {
            ushort expected = unchecked((ushort)(_lastSeq + 1));
            if (seq != expected)
            {
                int gap = (seq - expected + 65536) % 65536;
                DroppedPackets += gap;
            }
        }

        _lastSeq = seq;
        _seqInitialized = true;

        return new Sample(DateTime.UtcNow, seq, value);
    }

    // CRC-16 CCITT — matches PROTOCOL.md and simulator exactly.
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