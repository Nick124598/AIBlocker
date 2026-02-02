using System.Collections;
using System.Collections.Concurrent;
using System.DirectoryServices.ActiveDirectory;
using System.Net;
using System.Net.NetworkInformation;
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
        "midjourney", "claude", "copilot", "deepseek", "ai", "gemini"
    ];

    private readonly SemaphoreSlim _logSemaphore = new(1, 1);


    private readonly Dictionary<IPAddress, string> _ipToUser = new();

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
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up)
                continue;

            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                continue;

            var props = ni.GetIPProperties();

            bool hasIpv4Gateway = props.GatewayAddresses.Any(g =>
                g.Address.AddressFamily == AddressFamily.InterNetwork &&
                !g.Address.Equals(IPAddress.Any) &&
                !g.Address.Equals(IPAddress.Parse("0.0.0.0")));

            if (!hasIpv4Gateway)
                continue;

            var addr = props.UnicastAddresses
                .Select(u => u.Address)
                .FirstOrDefault(a =>
                    a.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(a) &&
                    !a.ToString().StartsWith("169.254.")); // APIPA

            if (addr != null)
                return addr.ToString();
        }

        return IPAddress.Loopback.ToString();
    }

    private string? LookupUsername(IPAddress clientIp)
    {
        return _ipToUser.TryGetValue(clientIp, out var username) ? username : null;
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

    private static byte[] BuildDnsQuery(string domain, ushort queryType = 1, ushort transactionId = 0)
    {
        if (transactionId == 0)
            transactionId = (ushort)Random.Shared.Next(1, 65536);

        // Encode domain name into DNS label format
        var labels = domain.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var qnameLength = labels.Sum(l => l.Length + 1) + 1; // +1 for length byte per label, +1 for null terminator

        var query = new byte[12 + qnameLength + 4]; // Header(12) + QNAME + QTYPE(2) + QCLASS(2)

        // --- DNS HEADER (12 bytes) ---
        // Transaction ID (2 bytes)
        query[0] = (byte)(transactionId >> 8);
        query[1] = (byte)(transactionId & 0xFF);

        // Flags (2 bytes): Standard query with recursion desired (0x0100)
        query[2] = 0x01; // RD (Recursion Desired) = 1
        query[3] = 0x00;

        // QDCOUNT (2 bytes): 1 question
        query[4] = 0x00;
        query[5] = 0x01;

        // ANCOUNT, NSCOUNT, ARCOUNT (6 bytes): all zeros
        // Already zeroed by array initialization

        // --- QUESTION SECTION ---
        var index = 12;

        // Encode QNAME (domain name in label format)
        foreach (var label in labels)
        {
            var labelBytes = Encoding.UTF8.GetBytes(label);
            query[index++] = (byte)labelBytes.Length;
            labelBytes.CopyTo(query.AsSpan(index));
            index += labelBytes.Length;
        }
        query[index++] = 0x00; // Null terminator

        // QTYPE (2 bytes)
        query[index++] = (byte)(queryType >> 8);
        query[index++] = (byte)(queryType & 0xFF);

        // QCLASS (2 bytes): IN (Internet) = 0x0001
        query[index++] = 0x00;
        query[index++] = 0x01;

        return query;
    }

    private async Task RunDnsServerAsync(string bindIp, CancellationToken ct)
    {
        IPEndPoint bindEp = new(IPAddress.Parse(bindIp), DnsPort);
        using var udp = new UdpClient(bindEp);
        while (true)
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
                IPEndPoint clientIp = received.RemoteEndPoint;
                var clientPort = received.RemoteEndPoint.Port;
                var domain = ExtractDomainName(received.Buffer);
                var domainLower = domain.ToLowerInvariant();
                
                var username = LookupUsername(clientIp.Address);
                var blocked = AiKeywords.Any(k => domainLower.Contains(k, StringComparison.Ordinal));
                var upstreamResp = Array.Empty<byte>();
                try
                {
                    if (blocked)
                    {
                        await _logSemaphore.WaitAsync(ct);
                        try {
                            await File.AppendAllTextAsync("log.txt",
            $"[{NowStr()}] User: {username} | IP: {clientIp} | Blocked: {domain}{Environment.NewLine}",
            ct);
                        } finally {
                            _logSemaphore.Release();
                        }

                        var redirectQuery = BuildDnsQuery("google.com");
                        upstreamResp = await ForwardDnsQueryAsync(redirectQuery, ct).ConfigureAwait(false);
                        
                    } else 
                        upstreamResp = await ForwardDnsQueryAsync(received.Buffer, ct).ConfigureAwait(false);
                    if (upstreamResp is null)
                    {
                        UiLog($"[{NowStr()}] TIMEOUT upstream={UpstreamDns} client={username} ip={clientIp} domain={domain}");
                        return;
                    }
                    await udp.SendAsync(upstreamResp, upstreamResp.Length, received.RemoteEndPoint).ConfigureAwait(false);
                    await _logSemaphore.WaitAsync(ct);
                    try
                    {
                        await File.AppendAllTextAsync(username + ".txt",
                            $"[{NowStr()}] User: {username} | IP: {clientIp} | domain: {domain}{Environment.NewLine}", ct);
                    }
                    finally 
                    {
                        _logSemaphore.Release();
                    }
                }
                catch (Exception ex)
                {
                    //UiLog($"[{NowStr()}] ERROR client={username} ip={clientIp} domain={domain} err={ex.Message}");
                    MessageBox.Show(username + " " + clientIp + " " + domain + " " + ex.Message);
                }
            }, ct);
        }
    }

    [Obsolete]
    private async Task ServerListen(string ip, CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Parse(ip), StudentsPort);
        listener.Start();

        var students = new List<string>();

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(ct);

                _ = Task.Run(async () =>
                {
                    try
                    {
                        using (client)
                        using (var stream = client.GetStream())
                        {
                            var buf = new byte[4096];
                            int len = await stream.ReadAsync(buf, ct);
                            var remoteIp = ((IPEndPoint)client.Client.RemoteEndPoint!).Address;
                            var username = LookupUsername(remoteIp);
                            var bufStr = Encoding.UTF8.GetString(buf, 0, len);
                            if (bufStr == "EXIT")
                            {
                                lock (students)
                                {
                                    if (username != null)
                                    {
                                        students.Remove(username);
                                        _logSemaphore.WaitAsync(ct);
                                        try
                                        {
                                            File.AppendAllTextAsync("log.txt",
                                                $"[{NowStr()}] User: {username} | IP: {remoteIp} | EXIT{Environment.NewLine}", ct);
                                            _ipToUser[client.Client.RemoteEndPoint is IPEndPoint remoteEndPoint ? remoteEndPoint.Address : IPAddress.None] = string.Empty;
                                            listBox1.Items.Clear();
                                            foreach (var s in students)
                                                listBox1.Items.Add(s);
                                        }
                                        finally
                                        {
                                            _logSemaphore.Release();
                                        }
                                        return;
                                    }
                                }
                            }

                            var data = JsonSerializer.Deserialize<Dictionary<string, string>>(bufStr);


                            if (data != null && data.TryGetValue("Username", out username) && !string.IsNullOrWhiteSpace(username))
                            {
                                lock (students)
                                    students.Add(username);
                                _ipToUser[remoteIp] = username;
                                await _logSemaphore.WaitAsync(ct);
                                try
                                {
                                    await File.AppendAllTextAsync("log.txt",
                    $"[{NowStr()}] User: {username} | IP: {remoteIp} | REGISTER{Environment.NewLine}",
                    ct);
                                }
                                finally
                                {
                                    _logSemaphore.Release();
                                }

                                client.GetStream().Write(Encoding.UTF8.GetBytes("OK"));
                                BeginInvoke((Action)(() =>
                                {
                                    listBox1.Items.Clear();
                                    lock (students)
                                    {
                                        foreach (var s in students)
                                            listBox1.Items.Add(s);
                                    }
                                }));

                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        UiLog($"[{NowStr()}] Student registration error: {ex.Message}");
                    }
                }, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
        finally
        {
            listener.Stop();
        }
    }


    private void Form1_Load(object sender, EventArgs e)
    {
        var ip = GetLocalIPv4OrLoopback();
        localiplabel.Text = ip;

        _cts = new CancellationTokenSource();

        _ = RunDnsServerAsync(ip, _cts.Token);
        _ = ServerListen(ip, _cts.Token);
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
