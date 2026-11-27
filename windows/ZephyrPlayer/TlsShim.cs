using System;

namespace ZephyrPlayer;

/// <summary>Log sink the shared secure-identity source (HelperTls.cs) calls as Program.Log. The elevated setup runs headless, so its lines go to stderr.</summary>
internal static class Program
{
    internal static void Log(string message) => Console.Error.WriteLine(message);
}
