/*
 * DeviceDiscovery.cs – Find a paired Bose QC Ultra via Windows Bluetooth APIs.
 *
 * Strategy (matches bosectl discovery.py logic):
 *   1. Enumerate all paired Bluetooth devices via DeviceInformation.
 *   2. For each device, check if it exposes the BMAP RFCOMM service UUID via SDP.
 *   3. Prefer a device whose SDP record matches; fall back to name prefix "Bose".
 *   4. Return the first connected/paired candidate.
 *
 * Windows API surface used:
 *   Windows.Devices.Bluetooth.BluetoothDevice          (WinRT, no extra NuGet)
 *   Windows.Devices.Bluetooth.Rfcomm.RfcommDeviceService
 *   Windows.Devices.Enumeration.DeviceInformation
 */

using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Devices.Enumeration;

namespace BoseCtl;

public sealed class DiscoveredDevice
{
    public string DeviceId          { get; }
    public string Name              { get; }
    public ulong  BluetoothAddress  { get; }
    public RfcommDeviceService RfcommService { get; }

    public DiscoveredDevice(string deviceId, string name,
        ulong bluetoothAddress, RfcommDeviceService rfcommService)
    {
        DeviceId         = deviceId;
        Name             = name;
        BluetoothAddress = bluetoothAddress;
        RfcommService    = rfcommService;
    }

    public string MacAddress =>
        string.Join(":", BitConverter.GetBytes(BluetoothAddress)
            .Take(6).Reverse().Select(b => b.ToString("X2")));
}

public static class DeviceDiscovery
{
    // ponytail: UUID filter is the most reliable selector across all BMAP devices.
    // Upgrade path: also check Bluetooth Modalias product ID (0x4082 for QC Ultra 2)
    // for disambiguation when multiple Bose devices are paired.
    private static readonly Guid BmapUuid = new("00000000-deca-fade-deca-deafdecacaff");

    /// <summary>
    /// Find the first paired, BMAP-capable Bluetooth device.
    /// Queries Windows device enumeration for BT classic devices, then
    /// checks each for the BMAP RFCOMM service via SDP.
    /// </summary>
    /// <returns>
    /// A <see cref="DiscoveredDevice"/> with an open RFCOMM service handle,
    /// or null if no device found. Caller must dispose the RfcommService.
    /// </returns>
    public static async Task<DiscoveredDevice?> FindAsync(bool verbose = false)
    {
        // AQS selector: all paired Bluetooth classic (BR/EDR) devices
        string selector = BluetoothDevice.GetDeviceSelectorFromPairingState(true);
        var devices = await DeviceInformation.FindAllAsync(selector);

        if (verbose)
            Console.WriteLine($"[discovery] {devices.Count} paired BT device(s) found");

        // Prefer devices with "Bose" in name for quick pre-filter before the SDP call
        var candidates = devices
            .OrderByDescending(d => d.Name.StartsWith("Bose", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var info in candidates)
        {
            if (verbose)
                Console.WriteLine($"[discovery] checking: {info.Name}  ({info.Id})");

            BluetoothDevice? btDev = null;
            try
            {
                btDev = await BluetoothDevice.FromIdAsync(info.Id);
                if (btDev is null) continue;

                var serviceResult = await btDev.GetRfcommServicesForIdAsync(
                    RfcommServiceId.FromUuid(BmapUuid),
                    BluetoothCacheMode.Uncached);     // live SDP query

                if (serviceResult.Services.Count == 0)
                {
                    if (verbose)
                        Console.WriteLine($"[discovery]   no BMAP service on {info.Name}");
                    continue;
                }

                // Take the first matching RFCOMM service
                var svc = serviceResult.Services[0];
                if (verbose)
                    Console.WriteLine($"[discovery]   BMAP service found on {info.Name}" +
                                      $" ch={svc.ConnectionHostName}:{svc.ConnectionServiceName}");

                return new DiscoveredDevice(info.Id, info.Name, btDev.BluetoothAddress, svc);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (verbose)
                    Console.WriteLine($"[discovery]   error probing {info.Name}: {ex.Message}");
                // Don't dispose svc here – it wasn't assigned to a DiscoveredDevice
                btDev?.Dispose();
            }
        }

        return null;
    }
}

