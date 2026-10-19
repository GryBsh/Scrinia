using System.Net.Sockets;
using System.Runtime.InteropServices;
using Scrinia.Commands;
using Scrinia.Core.Configuration;
using Scrinia.Mcp.SharedContracts;
using WorkspaceConfig = Scrinia.Core.Configuration.WorkspaceConfig;

namespace Scrinia.TestProcess;

internal static class SharedCarrierCommands
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 3 || !Path.IsPathFullyQualified(args[0]) || OperatingSystem.IsWindows())
            throw new ArgumentException("An owned Unix fixture directory, carrier and cache mode are required.");
        string root = args[0];
        var entries = new[] { CreateBase("bad"), CreateBase("healthy") };
        var registry = new Registry(entries);
        var workspace = new WorkspaceConfig(new Dictionary<string, string> { [WorkspaceConfig.EnabledKey] = "bad;healthy" });
        var factory = new SharedBaseRuntimeFactory();
        var server = new SharedServer(workspace, registry, factory);
        var tools = new SharedTools(server);
        Require((await tools.Remember("healthy", "/note", ["healthy payload"], description: "healthy payload")).Contains("mutation: applied"));
        // Seed with a separate factory so cold cases really do start without a cached bad runtime.
        var seed = new SharedTools(new SharedServer(workspace, registry, new SharedBaseRuntimeFactory()));
        Require((await seed.Remember("bad", "/private", ["private payload"], description: "private payload")).Contains("mutation: applied"));
        SharedBaseRuntime? cached = args[2] == "cached" ? factory.Open(entries[0]) : null;
        var before = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, File.ReadAllBytes);
        string special = Path.Combine(entries[0].Root, ".scrinia", "blocked");
        using var socket = args[1] == "socket" ? new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified) : null;
        if (socket is not null)
        {
            // macOS temporary roots can exceed sockaddr_un's short absolute-path limit.
            // This isolated child binds relative to its owned directory, then restores cwd.
            string previousDirectory = Environment.CurrentDirectory;
            try
            {
                Environment.CurrentDirectory = Path.GetDirectoryName(special)!;
                socket.Bind(new UnixDomainSocketEndPoint("blocked"));
            }
            finally { Environment.CurrentDirectory = previousDirectory; }
        }
        else if (args[1] != "fifo" || MkFifo(special, 0x180) != 0) throw new IOException("Cannot create owned FIFO.");
        await TestProcessProtocol.WriteEventAsync("carrier-ready");

        // Startup must get past the alphabetically first malformed base to a healthy base.
        new SharedServeHostAdapter(new HomeConfigStore(), workspace, registry, factory).ValidateStartup(server);
        foreach (string response in new[] { await tools.Recall("bad", "/private"), await tools.Refresh("bad"),
            await tools.List("bad"), await tools.Search("private", "bad"),
            await tools.Remember("bad", "/new", ["forbidden"], description: "forbidden") })
        {
            Require(response.Contains("SHARED_BASE_UNSAFE_PATH") && response.Contains("mutation: none"));
            Require(!response.Contains("private payload") && !response.Contains("results:") && !response.Contains("chunks:"));
        }
        string aggregate = await tools.List();
        Require(aggregate.Contains("status: partial") && aggregate.Contains("base: healthy") && aggregate.Contains("path: /note"));
        Require(aggregate.Contains("SHARED_BASE_UNSAFE_PATH") && !aggregate.Contains("private payload"));
        string bases = await tools.Bases();
        Require(bases.Contains("availability: unavailable") && bases.Contains("availability: available"));
        Require((await tools.Recall("healthy", "/note")).Contains("healthy payload"));
        try { SharedBaseRuntime.Fingerprint(entries[0].Root); throw new InvalidOperationException("Fingerprint accepted a special file."); }
        catch (ArgumentException) { }
        if (cached is not null)
        {
            var preflight = cached.RevalidateBeforeMutation(null, null);
            Require(!preflight.IsSafe && preflight.ErrorCode == "SHARED_BASE_UNSAFE_PATH");
        }
        Require(Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length == before.Count + 1);
        foreach (var (path, bytes) in before) Require(File.ReadAllBytes(path).SequenceEqual(bytes));
        await TestProcessProtocol.WriteEventAsync("verified");
        return 0;

        SharedBaseEntry CreateBase(string name)
        {
            string path = Path.Combine(root, name);
            Directory.CreateDirectory(Path.Combine(path, ".git"));
            Directory.CreateDirectory(Path.Combine(path, ".scrinia"));
            File.WriteAllText(Path.Combine(path, ".scrinia", ".gitignore"), ManagedIgnoreBlock.ProjectGit);
            return new SharedBaseEntry { Name = name, Root = path };
        }
    }

    private static void Require(bool condition)
    { if (!condition) throw new InvalidOperationException("Shared special-file refusal or isolation assertion failed."); }

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA2101", Justification = "libc paths use explicit UTF-8 marshaling.")]
    private static extern int MkFifo([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);

    private sealed class Registry(SharedBaseEntry[] entries) : ISharedBaseRegistry
    {
        public SharedBaseEntry? Lookup(string name) => entries.FirstOrDefault(entry => entry.Name == name);
        public IReadOnlyList<SharedBaseEntry> List(bool includeDisabled) => entries;
        public IReadOnlyList<SharedBaseCollision> Collisions() => [];
        public SharedBaseResult Add(string name, string root) => throw new NotSupportedException();
        public SharedBaseResult Remove(string name) => throw new NotSupportedException();
        public SharedBaseResult Enable(string name) => throw new NotSupportedException();
        public SharedBaseResult Disable(string name) => throw new NotSupportedException();
    }
}
