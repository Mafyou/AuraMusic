namespace AuraMusic.Kernel.IntegrationTests.Infrastructure;

/// <summary>
/// Both ends of a real TCP connection on 127.0.0.1: what the kernel sees of an RFCOMM socket or of a Wi-Fi link
/// is a <see cref="Stream"/>, and this one goes through the operating system's sockets like the real ones do.
/// </summary>
sealed class LoopbackLink : IDisposable
{
    /// <summary>A blocked read fails after this instead of hanging the test run.</summary>
    static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);

    readonly TcpClient masterClient;
    readonly TcpClient listenerClient;

    LoopbackLink(TcpClient masterClient, TcpClient listenerClient)
    {
        this.masterClient = masterClient;
        this.listenerClient = listenerClient;
        Master = Configure(masterClient);
        Listener = Configure(listenerClient);
    }

    /// <summary>The master's end of the link.</summary>
    public NetworkStream Master { get; }

    /// <summary>The listener's end of the link.</summary>
    public NetworkStream Listener { get; }

    public static async Task<LoopbackLink> OpenAsync(CancellationToken stoppingToken)
    {
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var listener = new TcpClient(AddressFamily.InterNetwork);
        var accepting = server.AcceptTcpClientAsync(stoppingToken);
        await listener.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)server.LocalEndpoint).Port, stoppingToken);
        var master = await accepting;
        return new LoopbackLink(master, listener);
    }

    /// <summary>The master hangs up (service stopped, out of range).</summary>
    public void CloseMaster() => masterClient.Dispose();

    /// <summary>The listener hangs up.</summary>
    public void CloseListener() => listenerClient.Dispose();

    static NetworkStream Configure(TcpClient client)
    {
        client.NoDelay = true; // as both apps do: 20 ms packets must not wait for Nagle
        var stream = client.GetStream();
        stream.ReadTimeout = (int)ReadTimeout.TotalMilliseconds;
        return stream;
    }

    public void Dispose()
    {
        masterClient.Dispose();
        listenerClient.Dispose();
    }
}
