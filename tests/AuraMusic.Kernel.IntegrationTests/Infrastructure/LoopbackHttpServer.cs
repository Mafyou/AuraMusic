namespace AuraMusic.Kernel.IntegrationTests.Infrastructure;

/// <summary>A canned HTTP answer.</summary>
/// <param name="HangUp">Closes the connection without answering.</param>
/// <param name="Stall">Never answers.</param>
readonly record struct HttpAnswer(int Status, string Body, bool HangUp = false, bool Stall = false);

/// <summary>
/// The smallest HTTP/1.1 server that answers the release feed's requests on 127.0.0.1, one connection per request,
/// so that <see cref="GitHubReleaseFeed"/> runs through a real <see cref="HttpClient"/> and real sockets without
/// ever reaching GitHub.
/// </summary>
sealed class LoopbackHttpServer : IDisposable
{
    readonly TcpListener tcp = new(IPAddress.Loopback, 0);
    readonly CancellationTokenSource stopping = new();
    readonly ConcurrentQueue<string> requests = new();

    public LoopbackHttpServer(HttpAnswer answer)
    {
        tcp.Start();
        _ = Task.Run(() => ServeAsync(answer, stopping.Token));
    }

    int Port => ((IPEndPoint)tcp.LocalEndpoint).Port;

    /// <summary>The head (request line and headers) of every request received.</summary>
    public IReadOnlyCollection<string> Requests => requests;

    /// <summary>A client whose every request lands on this server, path and query kept.</summary>
    public HttpClient CreateClient(TimeSpan? timeout = null) =>
        new(new RedirectToLoopback(Port, new SocketsHttpHandler())) { Timeout = timeout ?? TimeSpan.FromSeconds(10) };

    async Task ServeAsync(HttpAnswer answer, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await tcp.AcceptTcpClientAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    requests.Enqueue(await ReadHeadAsync(stream, stoppingToken));
                    if (answer.Stall)
                        await Task.Delay(Timeout.Infinite, stoppingToken);
                    if (answer.HangUp)
                        continue;
                    var body = Encoding.UTF8.GetBytes(answer.Body);
                    var head = Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 {answer.Status} Canned\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(head, stoppingToken);
                    await stream.WriteAsync(body, stoppingToken);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException)
                {
                    // The client gave up first: nothing to answer.
                }
            }
        }
    }

    static async Task<string> ReadHeadAsync(NetworkStream stream, CancellationToken stoppingToken)
    {
        var head = new StringBuilder();
        var buffer = new byte[1024];
        while (!head.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            int read = await stream.ReadAsync(buffer, stoppingToken);
            if (read == 0)
                break;
            head.Append(Encoding.ASCII.GetString(buffer, 0, read));
        }
        return head.ToString();
    }

    public void Dispose()
    {
        stopping.Cancel();
        tcp.Stop();
        stopping.Dispose();
    }

    /// <summary>Sends the request to the loopback server instead of its host, over plain HTTP.</summary>
    sealed class RedirectToLoopback(int port, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken stoppingToken)
        {
            request.RequestUri = new UriBuilder(request.RequestUri!) { Scheme = Uri.UriSchemeHttp, Host = "127.0.0.1", Port = port }.Uri;
            return base.SendAsync(request, stoppingToken);
        }
    }
}
