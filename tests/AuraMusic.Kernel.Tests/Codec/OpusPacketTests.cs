namespace AuraMusic.Kernel.Tests.Codec;

/// <summary>Packets in rented buffers, as the audio paths carry them.</summary>
public sealed class OpusPacketTests
{
    [Fact]
    public void Rent_CopiesThePacket()
    {
        byte[] source = [1, 2, 3, 4, 5];

        var packet = OpusPacket.Rent(source);

        packet.Length.ShouldBe(5);
        packet.Buffer.Length.ShouldBeGreaterThanOrEqualTo(5);
        packet.Buffer.ShouldNotBeSameAs(source);
        packet.Span.ToArray().ShouldBe(source);
        packet.Return();
    }

    [Fact]
    public void Span_LeavesOutTheRestOfTheBuffer()
    {
        var packet = new OpusPacket([9, 8, 7, 6], 2);

        packet.Span.ToArray().ShouldBe([9, 8]);
    }

    [Fact]
    public void FromArray_TakesTheWholeArray()
    {
        byte[] bytes = [1, 2, 3];

        OpusPacket packet = bytes;

        packet.Buffer.ShouldBeSameAs(bytes);
        packet.Length.ShouldBe(3);
    }

    [Fact]
    public void Rent_EmptyPacket_IsEmpty()
    {
        var packet = OpusPacket.Rent([]);

        packet.Span.IsEmpty.ShouldBeTrue();
        packet.Return();
    }
}
