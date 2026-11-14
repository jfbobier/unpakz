namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit
    {
    }
}

namespace Unpakz.Tests.Support
{
    internal static class MemoryStreamExtensions
    {
        /// <summary>The overload .NET Core has and .NET Framework lacks; the shared fixture builders use it.</summary>
        internal static void Write(this System.IO.MemoryStream stream, byte[] bytes) => stream.Write(bytes, 0, bytes.Length);

        internal static void Write(this System.IO.Compression.DeflateStream stream, byte[] bytes) => stream.Write(bytes, 0, bytes.Length);

        internal static bool EndsWith(this string text, char c) => text.Length > 0 && text[text.Length - 1] == c;

        internal static void Deconstruct<TKey, TValue>(this System.Collections.Generic.KeyValuePair<TKey, TValue> pair, out TKey key, out TValue value)
        {
            key = pair.Key;
            value = pair.Value;
        }
    }
}
