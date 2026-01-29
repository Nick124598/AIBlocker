using System.Collections;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace UIT;

public partial class Form1 : Form
{
    private const int DnsPort = 53;

    private const string UpstreamDns = "8.8.8.8";
    private const int UpstreamPort = 53;

    private const int StudentsPort = 5000;

    private static readonly TimeSpan MappingTtl = TimeSpan.FromHours(24);

    private static readonly string[] AiKeywords =
    [
        "chat", "gpt", "openai", "dall-e", "bard", "genai", "genie", "llm",
        "midjourney", "claude", "copilot", "deepseek", "ai"
    ];

    private sealed record UserMapping(string Name, DateTimeOffset LastSeen);

    // Kept for future use; without HTTP registration everything is "UNREGISTERED" unless you populate manually.
    private readonly ConcurrentDictionary<string, UserMapping> _ipToUser = new();

    private CancellationTokenSource? _cts;

    public Form1()
    {
        InitializeComponent();
    }

    private static string NowStr() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

    private void UiLog(string line)
    {
        if (IsDisposed) return;

        BeginInvoke((Action)(() =>
        {
            listBox1.Items.Add(line);
            listBox1.TopIndex = listBox1.Items.Count - 1;
        }));
    }

    private static string GetLocalIPv4OrLoopback()
    {
        var host = Dns.GetHostEntry(Dns.GetHostName());
        foreach (var ip in host.AddressList)
        {
            if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                return ip.ToString();
        }
        return "127.0.0.1";
    }

    private string LookupUsername(string clientIp)
    {
        if (_ipToUser.TryGetValue(clientIp, out var info))
            return info.Name;
        return "UNREGISTERED";
    }

    private static string ExtractDomainName(ReadOnlySpan<byte> query)
    {
        // Basic DNS QNAME parsing (no compression expected in queries).
        if (query.Length < 12) return string.Empty;

        var parts = new List<string>(8);
        var index = 12;

        while (index < query.Length)
        {
            var len = query[index++];
            if (len == 0) break;
            if (index + len > query.Length) return string.Empty;

            parts.Add(Encoding.UTF8.GetString(query.Slice(index, len)));
            index += len;
        }

        return string.Join('.', parts.Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    private static int GetQuestionLength(ReadOnlySpan<byte> query)
    {
        // Returns length of "question section" beginning at byte 12: QNAME + QTYPE(2) + QCLASS(2)
        if (query.Length < 12) return -1;

        var index = 12;
        while (index < query.Length)
        {
            var len = query[index++];
            if (len == 0) break;
            index += len;
            if (index > query.Length) return -1;
        }

        if (index + 4 > query.Length) return -1;
        return (index + 4) - 12;
    }

    private static byte[] BuildNxdomainResponse(ReadOnlySpan<byte> query)
    {
        if (query.Length < 12) return [];

        // Preserve RD bit from query flags
        ushort flags = (ushort)((query[2] << 8) | query[3]);
        ushort rd = (ushort)(flags & 0x0100);

        // QR=1, RA=1, RCODE=3 (NXDOMAIN), RD preserved
        ushort respFlags = (ushort)(0x8000 | rd | 0x0080 | 0x0003);

        var questionLen = GetQuestionLength(query);
        if (questionLen < 0) return [];

        var resp = new byte[12 + questionLen];

        // TXID
        resp[0] = query[0];
        resp[1] = query[1];

        // FLAGS
        resp[2] = (byte)(respFlags >> 8);
        resp[3] = (byte)(respFlags & 0xFF);

        // QDCOUNT copied
        resp[4] = query[4];
        resp[5] = query[5];

        // AN/NS/AR = 0
        resp[6] = resp[7] = 0;
        resp[8] = resp[9] = 0;
        resp[10] = resp[11] = 0;

        // Copy Question section
        query.Slice(12, questionLen).CopyTo(resp.AsSpan(12));

        return resp;
    }

    private async Task<byte[]?> ForwardDnsQueryAsync(byte[] query, CancellationToken ct)
    {
        using var upstream = new UdpClient(AddressFamily.InterNetwork);
        upstream.Connect(UpstreamDns, UpstreamPort);

        await upstream.SendAsync(query, ct).ConfigureAwait(false);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

        try
        {
            var result = await upstream.ReceiveAsync(timeoutCts.Token).ConfigureAwait(false);
            return result.Buffer;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private async Task CleanupExpiredMappingsLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var cutoff = DateTimeOffset.UtcNow - MappingTtl;
            foreach (var kvp in _ipToUser)
            {
                if (kvp.Value.LastSeen < cutoff)
                    _ipToUser.TryRemove(kvp.Key, out _);
            }
        }
    }

    private async Task RunDnsServerAsync(string bindIp, CancellationToken ct)
    {
        IPEndPoint bindEp = new(IPAddress.Parse(bindIp), DnsPort);
        using var udp = new UdpClient(bindEp);

        //UiLog($"[{NowStr()}] DNS Server running on {bindIp}:{DnsPort} (UDP)");
        //UiLog($"[{NowStr()}] Forwarding to {UpstreamDns}:{UpstreamPort}");

        //MessageBox.Show("Dns Server running on: " + bindIp + ": " + DnsPort);
        //MessageBox.Show("Forwarding to " + UpstreamDns +  ": " + UpstreamPort);

        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await udp.ReceiveAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }

            _ = Task.Run(async () =>
            {
                var clientIp = received.RemoteEndPoint.Address.ToString();
                var clientPort = received.RemoteEndPoint.Port;
                var domain = ExtractDomainName(received.Buffer);
                var domainLower = domain.ToLowerInvariant();

                var username = LookupUsername(clientIp);
                var blocked = AiKeywords.Any(k => domainLower.Contains(k, StringComparison.Ordinal));

                try
                {
                    if (blocked)
                    {
                        UiLog($"[{NowStr()}] BLOCK: user={username} ip={clientIp}:{clientPort} domain={domain}");

                        var resp = BuildNxdomainResponse(received.Buffer);
                        if (resp.Length > 0)
                            await udp.SendAsync(resp, resp.Length, received.RemoteEndPoint).ConfigureAwait(false);

                        return;
                    }

                    var upstreamResp = await ForwardDnsQueryAsync(received.Buffer, ct).ConfigureAwait(false);
                    if (upstreamResp is null)
                    {
                        UiLog($"[{NowStr()}] TIMEOUT upstream={UpstreamDns} client={username} ip={clientIp} domain={domain}");
                        return;
                    }

                    await udp.SendAsync(upstreamResp, upstreamResp.Length, received.RemoteEndPoint).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    UiLog($"[{NowStr()}] ERROR client={username} ip={clientIp} domain={domain} err={ex.Message}");
                }
            }, ct);
        }
    }

    private void Form1_Load(object sender, EventArgs e)
    {
        var ip = GetLocalIPv4OrLoopback();
        localiplabel.Text = ip;

        _cts = new CancellationTokenSource();

        _ = CleanupExpiredMappingsLoopAsync(_cts.Token);
        _ = RunDnsServerAsync(ip, _cts.Token);
        TcpListener listener = new TcpListener(IPAddress.Parse(ip), StudentsPort);
        listener.Start();
        ArrayList students = new ArrayList();
        while (true)
        {
            Socket socket = listener.AcceptSocket();
            byte[] msg = new byte[1024];
            socket.Close();
            int len = socket.Receive(msg);
            string jsonData = Encoding.UTF8.GetString(msg, 0, len);
            var data = JsonSerializer.Deserialize<Dictionary<string, string>>(jsonData);
            students.Add(data?["Username"]);
            listBox1.Items.Clear();
            foreach (string student in students)
                listBox1.Items.Add(student);
        }
    }                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                          

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _cts?.Cancel();
        base.OnFormClosing(e);
    }

    private void listBox1_SelectedIndexChanged(object sender, EventArgs e)
    {

    }
}
