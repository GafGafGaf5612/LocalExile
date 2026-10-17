using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

// PoEMitmProxy - a minimal non-elevated MITM for the PoE client.
//
// The client's libcurl honors HTTPS_PROXY. We accept CONNECT, terminate TLS with
// our (already machine-trusted) certificate, and relay the decrypted HTTP/1.1
// stream to a local backend:
//   patch.poecdn.com        -> PoEServer.Patch (plain HTTP)
//   *.login.pathofexile.com -> PoEServer.Auth  (plain HTTP)
// We log the first bytes of each client request so the protocol can be captured.

string pfx = Environment.GetEnvironmentVariable("POE_TLS_PFX")
    ?? throw new InvalidOperationException("POE_TLS_PFX not set");
string pw = Environment.GetEnvironmentVariable("POE_TLS_PFX_PASSWORD") ?? "";
int listenPort = int.TryParse(Environment.GetEnvironmentVariable("POE_MITM_PORT"), out var lp) ? lp : 8899;
string patchBackend = Environment.GetEnvironmentVariable("POE_PATCH_BACKEND") ?? "127.0.0.1:8080";
string authBackend = Environment.GetEnvironmentVariable("POE_AUTH_BACKEND") ?? "127.0.0.1:20484";

var cert = X509CertificateLoader.LoadPkcs12FromFile(pfx, pw);
var listener = new TcpListener(IPAddress.Loopback, listenPort);
listener.Start();
Console.WriteLine($"[mitm] listening 127.0.0.1:{listenPort}  patch->{patchBackend}  auth->{authBackend}");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

while (!cts.IsCancellationRequested)
{
    TcpClient c;
    try { c = await listener.AcceptTcpClientAsync(cts.Token); }
    catch (OperationCanceledException) { break; }
    _ = HandleAsync(c, cert, patchBackend, authBackend);
}

static async Task HandleAsync(TcpClient client, X509Certificate2 cert, string patchBackend, string authBackend)
{
    try
    {
        using (client)
        {
            var cs = client.GetStream();
            string connectLine = await ReadConnectAsync(cs);
            string host = ParseHost(connectLine);
            string backend = host.Contains("login", StringComparison.OrdinalIgnoreCase) ? authBackend : patchBackend;

            await cs.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n"));

            var ssl = new SslStream(cs, leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = cert,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                ApplicationProtocols = new List<SslApplicationProtocol> { SslApplicationProtocol.Http11 },
            });

            var (bh, bp) = SplitHostPort(backend);
            using var backendClient = new TcpClient();
            await backendClient.ConnectAsync(bh, bp);
            var bs = backendClient.GetStream();

            var up = RelayAsync(ssl, bs, $"{(host.StartsWith("patch") ? "P> " : "A> ")}");
            var down = RelayAsync(bs, ssl, null);
            await Task.WhenAny(up, down);
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[mitm] error {ex.GetType().Name}: {ex.Message}");
    }
}

static async Task RelayAsync(Stream from, Stream to, string? logPrefix)
{
    var buf = new byte[16384];
    bool logged = false;
    try
    {
        while (true)
        {
            int n = await from.ReadAsync(buf);
            if (n == 0) break;
            if (logPrefix is not null && !logged)
            {
                string head = Encoding.ASCII.GetString(buf, 0, Math.Min(n, 600)).Replace("\r\n", " | ");
                Console.WriteLine($"[{logPrefix}] {head}");
                logged = true;
            }
            await to.WriteAsync(buf.AsMemory(0, n));
            await to.FlushAsync();
        }
    }
    catch { /* connection closed */ }
}

static async Task<string> ReadConnectAsync(Stream s)
{
    var sb = new StringBuilder();
    var b = new byte[1];
    while (true)
    {
        int n = await s.ReadAsync(b);
        if (n == 0) break;
        sb.Append((char)b[0]);
        if (sb.Length >= 4 && sb.ToString(sb.Length - 4, 4) == "\r\n\r\n") break;
        if (sb.Length > 8192) break;
    }
    string text = sb.ToString();
    int idx = text.IndexOf("CONNECT ", StringComparison.OrdinalIgnoreCase);
    if (idx >= 0)
    {
        int end = text.IndexOf(' ', idx + 8);
        if (end > 0) return text[(idx + 8)..end];
    }
    return text.Split('\r')[0];
}

static string ParseHost(string connect) => connect.Trim();

static (string, int) SplitHostPort(string hp)
{
    var parts = hp.Split(':');
    return (parts[0], parts.Length > 1 ? int.Parse(parts[1]) : 443);
}
