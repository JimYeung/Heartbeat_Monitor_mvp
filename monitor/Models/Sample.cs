namespace Monitor.Models;

/// <summary>
/// One decoded packet from the device. Timestamp is applied on receive (PC clock).
/// </summary>
public sealed record Sample(DateTime Timestamp, ushort Sequence, short Value);
