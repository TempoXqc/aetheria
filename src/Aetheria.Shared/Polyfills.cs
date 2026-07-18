// Compatibility shims so Aetheria.Shared compiles both for net10.0 (server, tools) and
// netstandard2.1 (the Unity client consumes the netstandard DLL). Modern C# language features are
// fine on netstandard2.1 — only a few RUNTIME APIs need a fallback, centralized here.
//
// This file needs multiple namespaces, hence block-scoped declarations:
#pragma warning disable IDE0161 // Convert to file-scoped namespace

#if NETSTANDARD2_1
namespace System.Runtime.CompilerServices
{
    /// <summary>Enables C# 'init' accessors and records when targeting netstandard2.1.</summary>
    internal static class IsExternalInit
    {
    }
}
#endif

namespace Aetheria.Shared
{
    /// <summary>Argument guards (ArgumentNullException.ThrowIfNull is .NET 6+).</summary>
    internal static class Guard
    {
        public static void NotNull(object? value, string paramName)
        {
            if (value is null)
            {
                throw new ArgumentNullException(paramName);
            }
        }
    }

    /// <summary>Monotonic millisecond clock (Environment.TickCount64 is .NET Core 3.0+).</summary>
    internal static class SharedClock
    {
#if NETSTANDARD2_1
        private static readonly System.Diagnostics.Stopwatch Stopwatch = System.Diagnostics.Stopwatch.StartNew();

        public static long NowMs => Stopwatch.ElapsedMilliseconds;
#else
        public static long NowMs => Environment.TickCount64;
#endif
    }
}
