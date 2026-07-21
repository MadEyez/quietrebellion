/*
 * RfcommTransport.cs – Windows RFCOMM socket transport for BMAP devices.
 *
 * Port of bosectl/python/pybmap/transport.py.
 *
 * Uses WinRT StreamSocket over the RfcommDeviceService obtained from SDP.
 * All BMAP communication goes through SendRecvAsync:
 *   1. Send the packet.
 *   2. Wait up to <timeout> for the first response chunk.
 *   3. If drain=true, keep reading until a short inter-packet gap expires.
 *      This is required for commands that return multiple STATUS packets
 *      (e.g. GetAll modes [31.1] returns one [31.6] STATUS per mode).
 */

using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

namespace BoseCtl;

public sealed class RfcommTransport : IAsyncDisposable
{
    private readonly StreamSocket _socket;
    private readonly DataWriter   _writer;
    private readonly DataReader   _reader;
    private readonly bool         _debug;

    // 3s first-packet timeout, 0.4s drain gap – matches transport.py.
    // Upgrade path: make timeouts configurable if used as a library.
    private static readonly TimeSpan FirstPacketTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DrainGapTimeout    = TimeSpan.FromMilliseconds(400);

    // 200ms send→recv delay – matches bosectl Python transport.py (time.sleep(0.2)).
    // The QC Ultra firmware needs time to prepare the response after receiving a command.
    private static readonly TimeSpan SendRecvDelay = TimeSpan.FromMilliseconds(200);

    private RfcommTransport(StreamSocket socket, bool debug)
    {
        _socket = socket;
        _debug  = debug;
        _writer = new DataWriter(_socket.OutputStream);
        _reader = new DataReader(_socket.InputStream)
        {
            InputStreamOptions = InputStreamOptions.Partial,
        };
    }

    /// <summary>
    /// Open an RFCOMM connection to the given service (obtained via SDP discovery).
    /// </summary>
    public static async Task<RfcommTransport> ConnectAsync(
        RfcommDeviceService service, bool debug = false)
    {
        var socket = new StreamSocket();
        try
        {
            await socket.ConnectAsync(
                service.ConnectionHostName,
                service.ConnectionServiceName,
                // SocketProtectionLevel 0 = PlainText (unencrypted raw RFCOMM),
                // matching Linux AF_BLUETOOTH socket behavior.
                // BluetoothEncryptionAllowNullAuthentication (3) adds negotiation
                // bytes that confuse the BMAP device.
                (SocketProtectionLevel)0);
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        if (debug)
            Console.WriteLine($"[transport] connected to {service.ConnectionHostName}" +
                              $":{service.ConnectionServiceName}");

        var t = new RfcommTransport(socket, debug);
        await t.DrainConnectGreetingAsync();
        return t;
    }

    /// <summary>
    /// Drain any unsolicited bytes the device sends immediately on connect
    /// (e.g. the [255.85] SETGET greeting seen on QC Ultra).
    /// Uses a short 400ms window – if nothing arrives we just proceed.
    /// </summary>
    private async Task DrainConnectGreetingAsync()
    {
        using var cts = new CancellationTokenSource(DrainGapTimeout);
        try
        {
            uint got = await _reader.LoadAsync(4096).AsTask(cts.Token);
            if (got > 0)
            {
                var greeting = new byte[got];
                _reader.ReadBytes(greeting);
                if (_debug)
                    Console.WriteLine($"[transport] connect greeting (discarded): " +
                                      $"{Convert.ToHexString(greeting)}");
            }
        }
        catch (OperationCanceledException)
        {
            // No greeting within 400ms – that's fine, proceed
        }
    }

    /// <summary>
    /// Send <paramref name="packet"/> and return the raw response bytes.
    /// </summary>
    /// <param name="packet">Complete BMAP packet (built by <see cref="BmapProtocol.Build"/>).</param>
    /// <param name="drain">
    /// When true, keep reading until the inter-packet gap expires.
    /// Required for START [31.1] GetAllModes which returns multiple STATUS packets.
    /// </param>
    public async Task<byte[]> SendRecvAsync(byte[] packet, bool drain = false,
        CancellationToken ct = default)
    {
        if (_debug)
            Console.WriteLine($"[transport] TX: {Convert.ToHexString(packet)}");

        // ── Send ─────────────────────────────────────────────────────────────
        _writer.WriteBytes(packet);
        await _writer.StoreAsync().AsTask(ct);

        // 200ms delay between send and receive – matches bosectl Python transport.py.
        // The QC Ultra firmware buffers the command and needs time to prepare the
        // response. Without this delay, LoadAsync may return stale/empty data.
        await Task.Delay(SendRecvDelay, ct);

        // ── Receive first chunk ───────────────────────────────────────────────
        using var firstCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        firstCts.CancelAfter(FirstPacketTimeout);

        uint loaded;
        try
        {
            loaded = await _reader.LoadAsync(4096).AsTask(firstCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("No response from Bose device within timeout.");
        }

        var buf = new byte[loaded];
        _reader.ReadBytes(buf);
        var data = new List<byte>(buf);

        if (_debug && data.Count > 0)
            Console.WriteLine($"[transport] RX: {Convert.ToHexString(data.ToArray())}");

        // ── Drain additional packets ──────────────────────────────────────────
        if (drain)
        {
            while (true)
            {
                using var drainCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                drainCts.CancelAfter(DrainGapTimeout);
                try
                {
                    uint more = await _reader.LoadAsync(4096).AsTask(drainCts.Token);
                    if (more == 0) break;
                    var chunk = new byte[more];
                    _reader.ReadBytes(chunk);
                    data.AddRange(chunk);
                    if (_debug)
                        Console.WriteLine($"[transport] RX+: {Convert.ToHexString(chunk)}");
                }
                catch (OperationCanceledException)
                {
                    break;  // Gap timeout – no more packets
                }
            }
        }

        return data.ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        _writer.DetachStream();
        _writer.Dispose();
        _reader.DetachStream();
        _reader.Dispose();
        _socket.Dispose();
        await ValueTask.CompletedTask;
    }
}

