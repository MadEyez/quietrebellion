/*
 * WinsockRfcommTransport.cs – Raw AF_BTH / BTHPROTO_RFCOMM socket transport.
 *
 * Uses Windows Sockets 2 (ws2_32.dll) with AF_BTH = 32 and BTHPROTO_RFCOMM = 3
 * to connect directly to a specific RFCOMM channel number, bypassing SDP.
 *
 * This is the Windows equivalent of what bosectl/python does on Linux:
 *   sock = socket.socket(AF_BLUETOOTH, SOCK_STREAM, BTPROTO_RFCOMM)
 *   sock.connect((mac, 2))
 *
 * Why not WinRT StreamSocket?
 *   WinRT connects via SDP UUID → the resolved channel may differ from channel 2.
 *   All BMAP traffic must go to channel 2 (hardcoded in all bosectl implementations).
 *
 * SOCKADDR_BTH field layout (from ws2bth.h / bthsdpdef.h):
 *   USHORT  addressFamily    = AF_BTH (32)
 *   ULONGLONG btAddr         = 48-bit BT address (6 LSBytes, LE)
 *   GUID    serviceClassId   = {0} when using a fixed port
 *   ULONG   port             = RFCOMM channel number (1-30)
 *
 * The struct size varies by packing:
 *   pack=1 (pragma pack 1): 2+8+16+4 = 30 bytes  (no padding)
 *   pack=8 (Windows x64 default): 2+6pad+8+16+4 = 36 bytes
 * We try both at runtime to find the one the local Windows build accepts.
 */

using System.Runtime.InteropServices;
using System.Net.Sockets;

namespace BoseCtl;

public sealed class WinsockRfcommTransport : IAsyncDisposable
{
    // ── Winsock2 P/Invoke ─────────────────────────────────────────────────────
    private const int AF_BTH          = 32;
    private const int SOCK_STREAM     = 1;
    private const int BTHPROTO_RFCOMM = 3;
    private const nint INVALID_SOCKET = -1;  // (SOCKET)(~0) = all-bits-set
    private const int SOCKET_ERROR    = -1;
    private const int SOL_SOCKET      = 0xFFFF;
    private const int SO_RCVTIMEO     = 0x1006;
    private const int SO_SNDTIMEO     = 0x1005;
    private const int WSAETIMEDOUT    = 10060;

    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern nint socket(int af, int type, int protocol);

    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern int connect(nint s, nint name, int namelen);

    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern int setsockopt(nint s, int level, int optname,
        ref int optval, int optlen);

    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern int send(nint s, byte[] buf, int len, int flags);

    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern int recv(nint s, byte[] buf, int len, int flags);

    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern int closesocket(nint s);

    // ── Fields ────────────────────────────────────────────────────────────────
    private nint _sock = INVALID_SOCKET;
    private readonly bool _debug;

    private static readonly TimeSpan RecvTimeout   = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan SendRecvDelay = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan DrainGap      = TimeSpan.FromMilliseconds(400);

    private WinsockRfcommTransport(bool debug) { _debug = debug; }

    // ── Factory ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Open a raw RFCOMM socket directly to channel <paramref name="channel"/>
    /// (default 2, hardcoded for BMAP), bypassing SDP.
    /// </summary>
    /// <param name="btAddress">
    /// Bluetooth address as a <c>ulong</c> (the value from
    /// <c>BluetoothDevice.BluetoothAddress</c>, e.g. <c>0xE4AABB112233</c> for
    /// <c>E4:AA:BB:11:22:33</c>).
    /// </param>
    public static Task<WinsockRfcommTransport> ConnectAsync(
        ulong btAddress, int channel = 2, bool debug = false)
    {
        var t = new WinsockRfcommTransport(debug);
        t.Connect(btAddress, channel);
        return Task.FromResult(t);
    }

    private void Connect(ulong btAddress, int channel)
    {
        _sock = socket(AF_BTH, SOCK_STREAM, BTHPROTO_RFCOMM);
        if (_sock == INVALID_SOCKET)
            throw new SocketException(Marshal.GetLastWin32Error());

        // Set receive/send timeouts
        int timeoutMs = (int)RecvTimeout.TotalMilliseconds;
        setsockopt(_sock, SOL_SOCKET, SO_RCVTIMEO, ref timeoutMs, sizeof(int));
        setsockopt(_sock, SOL_SOCKET, SO_SNDTIMEO, ref timeoutMs, sizeof(int));

        if (_debug)
            Console.WriteLine(
                $"[transport] connecting  btAddr=0x{btAddress:X12}  ch={channel}  " +
                $"(raw AF_BTH RFCOMM)");

        // Try connecting with both known SOCKADDR_BTH sizes:
        //   30 bytes: pack=1 (no padding between ushort and ulong)
        //   36 bytes: pack=8 / x64 default (6 bytes padding after ushort)
        // We try 30 first; if the OS rejects it (WSAEADDRNOTAVAIL / WSAEINVAL),
        // we retry with 36.
        bool connected = false;
        int lastErr    = 0;
        foreach (int structSize in new[] { 30, 36 })
        {
            nint addr = BuildSockaddrBth(btAddress, (uint)channel, structSize);
            try
            {
                int rc = connect(_sock, addr, structSize);
                if (rc == SOCKET_ERROR)
                {
                    lastErr = Marshal.GetLastWin32Error();
                    if (_debug)
                        Console.WriteLine(
                            $"[transport] connect({structSize}B) → Winsock error {lastErr}");
                    // 10049 WSAEADDRNOTAVAIL / 10022 WSAEINVAL → try next size
                    if (lastErr is 10049 or 10022) continue;
                    // Any other error is a real failure (e.g. 10061 = refused)
                    break;
                }
                if (_debug)
                    Console.WriteLine(
                        $"[transport] connected on channel {channel}  " +
                        $"(SOCKADDR_BTH size={structSize})");
                connected = true;
                break;
            }
            finally
            {
                Marshal.FreeHGlobal(addr);
            }
        }

        if (!connected)
        {
            closesocket(_sock);
            _sock = INVALID_SOCKET;
            throw new SocketException(lastErr);
        }

        // Drain any unsolicited greeting bytes the device sends on connect
        DrainConnectGreeting();
    }

    // ── Send / Receive ────────────────────────────────────────────────────────

    public async Task<byte[]> SendRecvAsync(byte[] packet, bool drain = false,
        CancellationToken ct = default)
    {
        if (_debug)
            Console.WriteLine($"[transport] TX: {Convert.ToHexString(packet)}");

        // Send
        int sent = send(_sock, packet, packet.Length, 0);
        if (sent == SOCKET_ERROR)
            throw new SocketException(Marshal.GetLastWin32Error());

        // 200ms delay – matches bosectl Python transport.py time.sleep(0.2)
        await Task.Delay(SendRecvDelay, ct);

        // Receive first chunk
        byte[] buf = new byte[4096];
        int got = recv(_sock, buf, buf.Length, 0);
        if (got == SOCKET_ERROR)
        {
            int err = Marshal.GetLastWin32Error();
            if (err == WSAETIMEDOUT)
                throw new TimeoutException("No response from Bose device.");
            throw new SocketException(err);
        }

        var data = new List<byte>(new ArraySegment<byte>(buf, 0, got));
        if (_debug)
            Console.WriteLine($"[transport] RX: {Convert.ToHexString(data.ToArray())}");

        // Drain additional packets for START commands that return multiple STATUS
        if (drain)
        {
            int shortMs = (int)DrainGap.TotalMilliseconds;
            setsockopt(_sock, SOL_SOCKET, SO_RCVTIMEO, ref shortMs, sizeof(int));
            try
            {
                while (true)
                {
                    int more = recv(_sock, buf, buf.Length, 0);
                    if (more == SOCKET_ERROR) break;
                    var chunk = new ArraySegment<byte>(buf, 0, more);
                    data.AddRange(chunk);
                    if (_debug)
                        Console.WriteLine(
                            $"[transport] RX+: {Convert.ToHexString(chunk.ToArray())}");
                }
            }
            finally
            {
                int restoreMs = (int)RecvTimeout.TotalMilliseconds;
                setsockopt(_sock, SOL_SOCKET, SO_RCVTIMEO, ref restoreMs, sizeof(int));
            }
        }

        return data.ToArray();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Build a SOCKADDR_BTH in unmanaged memory with explicit byte placement.
    /// Caller is responsible for freeing the returned pointer with
    /// <see cref="Marshal.FreeHGlobal"/>.
    /// </summary>
    /// <param name="btAddress">48-bit BT address as ulong (MSB-first value).</param>
    /// <param name="port">RFCOMM channel number.</param>
    /// <param name="structSize">30 (pack=1) or 36 (pack=8/x64).</param>
    private static nint BuildSockaddrBth(ulong btAddress, uint port, int structSize)
    {
        // Field offsets for the two packing variants:
        //   pack=1 (30 bytes): af@0  btAddr@2   svcClass@10  port@26
        //   pack=8 (36 bytes): af@0  btAddr@8   svcClass@16  port@32
        int offBtAddr   = structSize == 30 ? 2  : 8;
        int offSvcClass = structSize == 30 ? 10 : 16;
        int offPort     = structSize == 30 ? 26 : 32;

        nint buf = Marshal.AllocHGlobal(structSize);
        // Zero fill (clears padding, svcClass = Guid.Empty, etc.)
        for (int i = 0; i < structSize; i++)
            Marshal.WriteByte(buf, i, 0);

        // addressFamily = AF_BTH = 32  (ushort, LE)
        Marshal.WriteInt16(buf, 0, unchecked((short)AF_BTH));

        // btAddr = 48-bit BT address in LE ULONGLONG
        // WinRT BluetoothAddress for E4:AA:BB:11:22:33 = 0x0000E4AABB112233
        Marshal.WriteInt64(buf, offBtAddr, unchecked((long)btAddress));

        // serviceClassId = Guid.Empty (already zeroed → SDP lookup disabled)

        // port = RFCOMM channel  (uint, LE)
        Marshal.WriteInt32(buf, offPort, unchecked((int)port));

        return buf;
    }

    /// <summary>
    /// Drain any unsolicited bytes the device sends immediately on connect
    /// (e.g. the [255.85] SETGET greeting seen on QC Ultra via WinRT).
    /// With raw channel-2 RFCOMM the device is usually silent on connect.
    /// </summary>
    private void DrainConnectGreeting()
    {
        int shortMs = (int)DrainGap.TotalMilliseconds;
        setsockopt(_sock, SOL_SOCKET, SO_RCVTIMEO, ref shortMs, sizeof(int));
        try
        {
            byte[] buf = new byte[256];
            int got = recv(_sock, buf, buf.Length, 0);
            if (got > 0 && _debug)
                Console.WriteLine(
                    $"[transport] connect greeting (discarded): " +
                    $"{Convert.ToHexString(new ArraySegment<byte>(buf, 0, got).ToArray())}");
        }
        catch { /* WSAETIMEDOUT = no greeting, proceed */ }
        finally
        {
            int restoreMs = (int)RecvTimeout.TotalMilliseconds;
            setsockopt(_sock, SOL_SOCKET, SO_RCVTIMEO, ref restoreMs, sizeof(int));
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_sock != INVALID_SOCKET)
        {
            closesocket(_sock);
            _sock = INVALID_SOCKET;
        }
        return ValueTask.CompletedTask;
    }
}

