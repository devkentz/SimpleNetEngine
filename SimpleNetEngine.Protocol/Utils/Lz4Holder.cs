namespace SimpleNetEngine.Protocol.Utils;

public class Lz4Holder
{
    // 쓰레드마다 독립된 Lz4 인스턴스를 유지
    private static readonly ThreadLocal<Lz4> Lz4 = new(() => new Lz4());

    public static Lz4Holder Instance { get; } = new();

    private Lz4Holder() {}

    public ReadOnlySpan<byte> Compress(ReadOnlySpan<byte> input)
    {
        return Lz4.Value!.Compress(input);
    }

    public ReadOnlySpan<byte> Decompress(ReadOnlySpan<byte> compressed, int originalSize)
    {
        return Lz4.Value!.Decompress(compressed, originalSize);
    }
    
    public  int Decompress(ReadOnlySpan<byte> compressed, Span<byte> output)
    {
        return Lz4.Value!.Decompress(compressed, output);
    }
}