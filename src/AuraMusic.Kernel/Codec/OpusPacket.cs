namespace AuraMusic.Kernel.Codec;

/// <summary>
/// One Opus packet in a buffer that may be longer than the packet: on the audio paths the buffer is rented from
/// <see cref="ArrayPool{T}.Shared"/> rather than allocated, 50 times a second per link. Whoever holds the packet
/// last gives the buffer back with <see cref="Return"/>, once, and never reads it afterwards.
/// </summary>
public readonly record struct OpusPacket(byte[] Buffer, int Length)
{
    /// <summary>The packet itself.</summary>
    public ReadOnlySpan<byte> Span => Buffer.AsSpan(0, Length);

    /// <summary>A copy of <paramref name="packet"/> in a rented buffer, to <see cref="Return"/> once used.</summary>
    public static OpusPacket Rent(ReadOnlySpan<byte> packet)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(packet.Length);
        packet.CopyTo(buffer);
        return new OpusPacket(buffer, packet.Length);
    }

    /// <summary>Gives a buffer that came from <see cref="Rent"/> back to the pool.</summary>
    public void Return() => ArrayPool<byte>.Shared.Return(Buffer);

    /// <summary>A packet that fills its whole array, which is not rented (tests, one-off packets).</summary>
    public static implicit operator OpusPacket(byte[] packet) => new(packet, packet.Length);
}
