namespace Lane.Testing;

/// <summary>
/// This project is a library everywhere except here.
///
/// It is also an executable so that <see cref="FakeMcpServer"/> can be launched as a child
/// process, which is the only way to test the MCP client against a server that really does
/// speak JSON-RPC over a pipe and really can crash.
/// </summary>
public static class Program
{
    public static int Main(string[] args) => FakeMcpServer.TryRun(args) ? 0 : 64;
}
