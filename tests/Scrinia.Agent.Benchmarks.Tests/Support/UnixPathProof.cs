using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Scrinia.Agent.Benchmarks.Tests;

// Run directly with a native Unix runtime; no test-platform SDK is required.
internal static class UnixPathProof
{
    internal static int Run()
    {
        if (OperatingSystem.IsWindows()) return 2;
        using var temp = new ScratchDirectory();
        string regular = Path.Combine(temp.Root, "regular");
        File.WriteAllText(regular, "ordinary file");
        PathSafety.RequireNoLinks(regular);
        PathSafety.RequireNoLinks(temp.Root);
        PathSafety.RequireNoLinks(Path.Combine(temp.Root, "missing"));
        string fifo = Path.Combine(temp.Root, "fifo");
        if (MkFifo(fifo, 384) != 0) throw new IOException("Could not create the owned FIFO fixture.");
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        string endpoint = Path.Combine(temp.Root, "socket");
        socket.Bind(new UnixDomainSocketEndPoint(endpoint));
        string link = Path.Combine(temp.Root, "link");
        File.CreateSymbolicLink(link, regular);
        foreach (string path in new[] { fifo, endpoint, link, "/dev/null" })
        {
            try { PathSafety.RequireNoLinks(path); }
            catch (InvalidDataException) { continue; }
            Console.Error.WriteLine("Accepted special file: " + Path.GetFileName(path));
            return 1;
        }
        Console.WriteLine("Unix path proof passed: regular/directory/missing accepted; FIFO/socket/symlink/device rejected without opening.");
        return 0;
    }

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA2101", Justification = "Unix paths are explicitly marshalled as UTF-8.")]
    private static extern int MkFifo([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);
}
