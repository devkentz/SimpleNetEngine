using System.Text;

namespace SimpleNetEngine.Protocol.Utils;

public static class HashHelper
{
    public static class StringHasher
    {
        private const int MaxUtf8Length = 1024;

        public static int Hash32(string messageName)
        {
            if (string.IsNullOrEmpty(messageName))
                throw new ArgumentException("Message name cannot be null or empty", nameof(messageName));

            Span<byte> utf8Buffer = stackalloc byte[MaxUtf8Length];
            int byteCount = Encoding.UTF8.GetBytes(messageName, utf8Buffer);
        
            if (byteCount > MaxUtf8Length)
                throw new ArgumentException($"UTF-8 encoding exceeds {MaxUtf8Length} bytes");

            return XxHash32(utf8Buffer[..byteCount]);
        }

        public static long Hash64(string messageName)
        {
            if (string.IsNullOrEmpty(messageName))
                throw new ArgumentException("Message name cannot be null or empty", nameof(messageName));

            Span<byte> utf8Buffer = stackalloc byte[MaxUtf8Length];
            int byteCount = Encoding.UTF8.GetBytes(messageName, utf8Buffer);
        
            if (byteCount > MaxUtf8Length)
                throw new ArgumentException($"UTF-8 encoding exceeds {MaxUtf8Length} bytes");

            return XxHash64(utf8Buffer[..byteCount]);
        }

        public static uint Hash32AsUInt(string messageName) 
            => unchecked((uint)Hash32(messageName));
    
        public static ulong Hash64AsULong(string messageName) 
            => unchecked((ulong)Hash64(messageName));
    }

    private static int XxHash32(ReadOnlySpan<byte> data) =>
        unchecked((int) System.IO.Hashing.XxHash32.HashToUInt32(data));

    public static long XxHash64(ReadOnlySpan<byte> data) =>
        unchecked((long) System.IO.Hashing.XxHash64.HashToUInt64(data));
}