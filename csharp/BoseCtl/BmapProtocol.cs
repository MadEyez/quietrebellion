/*
 * BmapProtocol.cs – BMAP packet encoding and decoding.
 *
 * Direct port of bosectl/python/pybmap/protocol.py and constants.py.
 *
 * BMAP (Bose Messaging and Protocol) is the proprietary application-layer
 * protocol spoken over RFCOMM channel 2 on all Bose Bluetooth devices.
 *
 * Packet wire format (big-endian, variable length):
 *   Byte 0   fblock_id   – function-block selector (e.g. 2 = Battery, 31 = AudioModes)
 *   Byte 1   func_id     – function within the block (e.g. 2 = level, 3 = current mode)
 *   Byte 2   flags       – (device_id << 6) | (port_num << 4) | (operator & 0x0F)
 *                          device_id and port_num are 0 in practice for host→device.
 *   Byte 3   length      – payload byte count (0-255)
 *   Byte 4…  payload     – function-specific data
 *
 * Operators (low nibble of flags):
 *   0 SET        Write a value (requires cloud-mediated ECDH auth on most blocks)
 *   1 GET        Read a value  (no auth required)
 *   2 SETGET     Write + read back (no auth on Settings [1.x] and AudioModes [31.x])
 *   3 STATUS     Unsolicited notification pushed by device
 *   4 ERROR      Error response (payload[0] = error code)
 *   5 START      Trigger an action
 *   6 RESULT     Action completed successfully
 *   7 PROCESSING Async operation in progress
 */

namespace BoseCtl;

/// <summary>BMAP operator codes (low nibble of the flags byte).</summary>
public static class Op
{
    public const byte Set        = 0;
    public const byte Get        = 1;
    public const byte SetGet     = 2;
    public const byte Status     = 3;
    public const byte Error      = 4;
    public const byte Start      = 5;
    public const byte Result     = 6;
    public const byte Processing = 7;

    public static string Name(byte op) => op switch
    {
        Set        => "SET",
        Get        => "GET",
        SetGet     => "SETGET",
        Status     => "STATUS",
        Error      => "ERROR",
        Start      => "START",
        Result     => "RESULT",
        Processing => "PROCESSING",
        _          => $"op{op}",
    };
}

/// <summary>BMAP error codes returned in ERROR (op 4) responses.</summary>
public static class BmapError
{
    public static string Name(byte code) => code switch
    {
        0  => "Unknown",
        1  => "Length",
        2  => "Chksum",
        3  => "FblockNotSupp",
        4  => "FuncNotSupp",
        5  => "OpNotSupp(auth)",
        6  => "InvalidData",
        7  => "DataUnavail",
        8  => "Runtime",
        9  => "Timeout",
        10 => "InvalidState",
        15 => "InvalidTransition",
        20 => "InsecureTransport",
        _  => $"err{code}",
    };
}

/// <summary>Decoded BMAP packet.</summary>
public sealed record BmapResponse(
    byte FBlock,
    byte Func,
    byte Operator,
    byte[] Payload)
{
    /// <summary>Human-readable summary for debug logging.</summary>
    public string Format()
    {
        string opName = Op.Name(Operator);
        string hex    = Convert.ToHexString(Payload).ToLowerInvariant();
        if (Operator == Op.Error && Payload.Length > 0)
            return $"[{FBlock}.{Func}] {opName}: {BmapError.Name(Payload[0])} ({hex})";
        return $"[{FBlock}.{Func}] {opName}: {hex}";
    }
}

/// <summary>
/// Stateless helpers for building and parsing BMAP packets.
/// No I/O, no device-specific knowledge.
/// </summary>
public static class BmapProtocol
{
    /// <summary>
    /// Build a BMAP packet ready to send over RFCOMM.
    /// </summary>
    /// <param name="fblock">Function block ID (0–255).</param>
    /// <param name="func">Function ID within the block (0–255).</param>
    /// <param name="op">Operator (use <see cref="Op"/> constants).</param>
    /// <param name="payload">Optional payload bytes. Defaults to empty.</param>
    public static byte[] Build(byte fblock, byte func, byte op, ReadOnlySpan<byte> payload = default)
    {
        // bmap: wire format [fblock, func, flags, length, ...payload]
        // flags = op & 0x0F  (device_id=0, port_num=0 for host→device)
        byte flags = (byte)(op & 0x0F);
        var pkt = new byte[4 + payload.Length];
        pkt[0] = fblock;
        pkt[1] = func;
        pkt[2] = flags;
        pkt[3] = (byte)payload.Length;
        payload.CopyTo(pkt.AsSpan(4));
        return pkt;
    }

    /// <summary>
    /// Parse one BMAP response from the start of <paramref name="data"/>.
    /// Returns null if data is shorter than the minimum 4-byte header.
    /// </summary>
    public static BmapResponse? Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4) return null;
        byte fblock  = data[0];
        byte func    = data[1];
        byte op      = (byte)(data[2] & 0x0F);
        int  length  = data[3];
        int  end     = 4 + length;
        if (data.Length < end) return null;
        return new BmapResponse(fblock, func, op, data[4..end].ToArray());
    }

    /// <summary>
    /// Parse all concatenated BMAP packets from <paramref name="data"/>.
    /// Devices often push multiple STATUS packets back-to-back (e.g. GetAll).
    /// </summary>
    public static List<BmapResponse> ParseAll(ReadOnlySpan<byte> data)
    {
        var results = new List<BmapResponse>();
        int pos = 0;
        while (pos + 4 <= data.Length)
        {
            byte op     = (byte)(data[pos + 2] & 0x0F);
            int  length = data[pos + 3];
            int  end    = pos + 4 + length;
            if (end > data.Length) break;  // truncated packet – stop
            results.Add(new BmapResponse(
                data[pos], data[pos + 1], op,
                data[(pos + 4)..end].ToArray()));
            pos = end;
        }
        return results;
    }
}

