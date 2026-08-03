using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace HerculesWaveBridge;

internal interface IHerculesTransport : IDisposable
{
    event EventHandler<DeviceInputEvent>? InputReceived;
    event EventHandler<TransportStatus>? StatusChanged;
    string Mode { get; }
    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync();
    Task SendDisplayFrameAsync(Image image, IReadOnlyList<ChannelSlot> slots, BridgeStatus status, CancellationToken cancellationToken);
}

internal sealed class HerculesTransport : IHerculesTransport
{
    private const int VendorReadFailureDisconnectThreshold = 50;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint PipeTransferTimeout = 3;
    private const uint VendorIoctlReadPacket = 0x001E2000;
    private const uint VendorIoctlQueryPacketSize = 0x001E200C;
    private const uint VendorIoctlQueryIsochSize = 0x001E2008;
    private const uint VendorIoctlQueryUnknown = 0x001E2010;
    private const uint VendorIoctlStartA = 0x001E2024;
    private const uint VendorIoctlStartB = 0x001E2034;
    private const string VendorMainInterfaceGuid = "abee8c27-397b-409d-b6dc-6161ca409d8d";
    private static readonly TimeSpan VendorPollDelay = TimeSpan.FromMilliseconds(GetIntEnvironment("HWB_VENDOR_POLL_MS", 4, 1, 100));

    private readonly BridgeLogger _logger;
    private readonly PacketMapDecoder _decoder;
    private readonly object _lock = new();
    private readonly object _vendorIoLock = new();
    private CancellationTokenSource? _readerCts;
    private FileStream? _stream;
    private SafeFileHandle? _handle;
    private IntPtr _winUsbHandle;
    private readonly List<IntPtr> _winUsbInterfaceHandles = [];
    private readonly List<WinUsbReadTarget> _winUsbReadTargets = [];
    private byte _winUsbInPipeId;
    private byte _winUsbOutPipeId;

    public HerculesTransport(BridgeLogger logger)
    {
        _logger = logger;
        _decoder = PacketMapDecoder.Load(logger);
    }

    public event EventHandler<DeviceInputEvent>? InputReceived;
    public event EventHandler<TransportStatus>? StatusChanged;

    public string Mode { get; private set; } = "Not connected";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (_stream is not null)
            {
                return;
            }
        }

        if (Process.GetProcessesByName("Stream-control").Length > 0)
        {
            SetStatus(false, "Blocked", "Hercules Stream Control is running. Close it before taking over the hardware.");
            return;
        }

        var paths = DeviceInterfaceDiscovery.FindHerculesDevicePaths();
        _logger.Info($"Found {paths.Count} Hercules device interface path(s).");
        if (TryStartVendorDriver(paths))
        {
            return;
        }

        foreach (var path in paths)
        {
            _logger.Info($"Candidate device path: {path}");
        }

        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var handle = CreateFile(path, GenericRead | GenericWrite, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, FileFlagOverlapped, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                _logger.Warn($"Open failed for {path}: {new Win32Exception(error).Message} ({error})");
                handle.Dispose();
                continue;
            }

            try
            {
                var stream = new FileStream(handle, FileAccess.ReadWrite, 4096, isAsync: true);
                if (!await ProbeReadSupportAsync(stream, cancellationToken).ConfigureAwait(false))
                {
                    stream.Dispose();
                    handle.Dispose();
                    continue;
                }

                lock (_lock)
                {
                    _handle = handle;
                    _stream = stream;
                    _readerCts = new CancellationTokenSource();
                }

                SetStatus(true, "Existing driver interface", "Connected to a Hercules device interface. Waiting for packets.");
                _ = Task.Run(() => ReadLoopAsync(stream, _readerCts.Token), CancellationToken.None);
                return;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Could not create stream for {path}");
                handle.Dispose();
            }
        }

        if (TryStartWinUsb(paths))
        {
            return;
        }

        SetStatus(false, "Probe only", "No readable existing-driver or WinUSB device interface opened. Rebind to WinUSB or capture traffic to continue.");
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        FileStream? stream;
        SafeFileHandle? handle;
        List<IntPtr> winUsbInterfaceHandles;
        bool wasActive;

        lock (_lock)
        {
            cts = _readerCts;
            stream = _stream;
            handle = _handle;
            winUsbInterfaceHandles = [.. _winUsbInterfaceHandles];
            wasActive = cts is not null || stream is not null || handle is not null || _winUsbHandle != IntPtr.Zero || winUsbInterfaceHandles.Count > 0;
            _readerCts = null;
            _stream = null;
            _handle = null;
            _winUsbHandle = IntPtr.Zero;
            _winUsbInterfaceHandles.Clear();
            _winUsbReadTargets.Clear();
            _winUsbInPipeId = 0;
            _winUsbOutPipeId = 0;
        }

        if (cts is not null)
        {
            await cts.CancelAsync().ConfigureAwait(false);
            cts.Dispose();
        }

        foreach (var interfaceHandle in winUsbInterfaceHandles)
        {
            WinUsb_Free(interfaceHandle);
        }

        stream?.Dispose();
        handle?.Dispose();
        if (wasActive)
        {
            SetStatus(false, "Stopped", "Hardware transport stopped.");
        }
    }

    public Task SendDisplayFrameAsync(Image image, IReadOnlyList<ChannelSlot> slots, BridgeStatus status, CancellationToken cancellationToken)
    {
        var previewPath = _logger.SaveDisplayPreview(image);
        _logger.Info($"LCD preview rendered to {previewPath}; the production native display loop owns physical screen updates.");
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
    }

    private async Task ReadLoopAsync(FileStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[512];
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    continue;
                }

                var packet = buffer.AsSpan(0, read).ToArray();
                var events = _decoder.Decode(packet);
                LogDecodedInput("RX", read, packet, events);
                foreach (var inputEvent in events)
                {
                    InputReceived?.Invoke(this, inputEvent);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Hardware read loop failed");
                SetStatus(false, Mode, $"Read loop failed: {ex.Message}");
                return;
            }
        }
    }

    private bool TryStartWinUsb(IReadOnlyList<string> paths)
    {
        foreach (var path in paths)
        {
            var handle = CreateFile(path, GenericRead | GenericWrite, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, FileFlagOverlapped, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                continue;
            }

            if (!WinUsb_Initialize(handle, out var winUsbHandle))
            {
                var error = Marshal.GetLastWin32Error();
                _logger.Warn($"WinUSB initialize failed for {path}: {new Win32Exception(error).Message} ({error})");
                handle.Dispose();
                continue;
            }

            if (!WinUsb_QueryInterfaceSettings(winUsbHandle, 0, out var descriptor))
            {
                var error = Marshal.GetLastWin32Error();
                _logger.Warn($"WinUSB interface query failed for {path}: {new Win32Exception(error).Message} ({error})");
                WinUsb_Free(winUsbHandle);
                handle.Dispose();
                continue;
            }

            var interfaceHandles = new List<IntPtr> { winUsbHandle };
            var readTargets = new List<WinUsbReadTarget>();
            byte outPipe = 0;
            CollectWinUsbPipes(winUsbHandle, "if0", descriptor, readTargets, ref outPipe);

            for (byte associatedIndex = 0; associatedIndex < 16; associatedIndex++)
            {
                if (!WinUsb_GetAssociatedInterface(winUsbHandle, associatedIndex, out var associatedHandle))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error != 259)
                    {
                        _logger.Warn($"WinUSB associated interface {associatedIndex} unavailable: {new Win32Exception(error).Message} ({error})");
                    }

                    break;
                }

                interfaceHandles.Add(associatedHandle);
                if (!WinUsb_QueryInterfaceSettings(associatedHandle, 0, out var associatedDescriptor))
                {
                    var error = Marshal.GetLastWin32Error();
                    _logger.Warn($"WinUSB associated interface {associatedIndex} query failed: {new Win32Exception(error).Message} ({error})");
                    continue;
                }

                CollectWinUsbPipes(associatedHandle, $"assoc{associatedIndex}", associatedDescriptor, readTargets, ref outPipe);
            }

            if (readTargets.Count == 0)
            {
                _logger.Warn($"WinUSB opened {path}, but no IN endpoints were found.");
                foreach (var interfaceHandle in interfaceHandles)
                {
                    WinUsb_Free(interfaceHandle);
                }

                handle.Dispose();
                continue;
            }

            var timeoutMs = 1000u;
            foreach (var target in readTargets)
            {
                if (!WinUsb_SetPipePolicy(target.InterfaceHandle, target.PipeId, PipeTransferTimeout, sizeof(uint), ref timeoutMs))
                {
                    var error = Marshal.GetLastWin32Error();
                    _logger.Warn($"Could not set WinUSB read timeout on {target.Label} pipe 0x{target.PipeId:X2}: {new Win32Exception(error).Message} ({error})");
                }
            }

            lock (_lock)
            {
                _handle = handle;
                _winUsbHandle = winUsbHandle;
                _winUsbInterfaceHandles.AddRange(interfaceHandles);
                _winUsbReadTargets.AddRange(readTargets);
                _winUsbInPipeId = readTargets[0].PipeId;
                _winUsbOutPipeId = outPipe;
                _readerCts = new CancellationTokenSource();
            }

            SetStatus(true, "WinUSB", $"Connected through WinUSB. Reading {readTargets.Count} IN pipe(s).");
            foreach (var target in readTargets)
            {
                _ = Task.Run(() => WinUsbReadLoop(target, _readerCts.Token), CancellationToken.None);
            }

            return true;
        }

        return false;
    }

    private bool TryStartVendorDriver(IReadOnlyList<string> paths)
    {
        foreach (var path in paths.Where(path => path.Contains(VendorMainInterfaceGuid, StringComparison.OrdinalIgnoreCase)))
        {
            var handle = CreateFile(path, GenericRead | GenericWrite, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, FileFlagOverlapped, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                _logger.Warn($"Vendor-driver open failed for {path}: {new Win32Exception(error).Message} ({error})");
                handle.Dispose();
                continue;
            }

            try
            {
                PrimeVendorDriver(handle);
                lock (_lock)
                {
                    _handle = handle;
                    _readerCts = new CancellationTokenSource();
                }

                SetStatus(true, "Hercules vendor driver", $"Connected to the official driver interface. Polling Stream 100 input packets every {VendorPollDelay.TotalMilliseconds:0} ms.");
                _ = Task.Run(() => VendorDriverPollLoop(handle, _readerCts.Token), CancellationToken.None);
                return true;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Could not initialize vendor-driver interface {path}");
                handle.Dispose();
            }
        }

        return false;
    }

    private void PrimeVendorDriver(SafeFileHandle handle)
    {
        Span<(uint Code, byte[]? Input, int OutputLength)> calls =
        [
            (VendorIoctlQueryPacketSize, null, 4),
            (VendorIoctlQueryIsochSize, null, 4),
            (VendorIoctlQueryUnknown, null, 4),
            (VendorIoctlStartA, [0, 0, 0, 0], 0),
            (VendorIoctlStartB, [0, 0, 0, 0], 0)
        ];

        foreach (var (code, input, outputLength) in calls)
        {
            var output = outputLength > 0 ? new byte[outputLength] : null;
            bool ok;
            int bytesReturned;
            lock (_vendorIoLock)
            {
                ok = DeviceIoControl(handle, code, input, input?.Length ?? 0, output, output?.Length ?? 0, out bytesReturned, IntPtr.Zero);
            }

            if (!ok)
            {
                var error = Marshal.GetLastWin32Error();
                _logger.Warn($"Vendor-driver init IOCTL 0x{code:X8} failed: {new Win32Exception(error).Message} ({error})");
                continue;
            }

            if (output is not null)
            {
                _logger.Info($"Vendor-driver init IOCTL 0x{code:X8}: {Convert.ToHexString(output.AsSpan(0, bytesReturned))}");
            }
            else
            {
                _logger.Info($"Vendor-driver init IOCTL 0x{code:X8}: ok");
            }
        }
    }

    private async Task VendorDriverPollLoop(SafeFileHandle handle, CancellationToken cancellationToken)
    {
        var buffer = new byte[64];
        var consecutiveReadFailures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                bool ok;
                int bytesReturned;
                lock (_vendorIoLock)
                {
                    ok = DeviceIoControl(handle, VendorIoctlReadPacket, null, 0, buffer, buffer.Length, out bytesReturned, IntPtr.Zero);
                }

                if (!ok)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    var error = Marshal.GetLastWin32Error();
                    var message = new Win32Exception(error).Message;
                    consecutiveReadFailures++;
                    if (consecutiveReadFailures <= 5 || consecutiveReadFailures % 25 == 0)
                    {
                        _logger.Warn($"Vendor-driver packet read failed ({consecutiveReadFailures}/{VendorReadFailureDisconnectThreshold}): {message} ({error})");
                    }

                    if (consecutiveReadFailures >= VendorReadFailureDisconnectThreshold)
                    {
                        _logger.Warn("Vendor-driver packet read failure threshold reached; marking hardware disconnected so reconnect can reopen the device.");
                        SetStatus(false, "Hercules vendor driver", $"Vendor-driver packet read failed: {message}");
                        return;
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                consecutiveReadFailures = 0;
                if (bytesReturned > 0)
                {
                    var packet = buffer.AsSpan(0, bytesReturned).ToArray();
                    var events = _decoder.Decode(packet);
                    if (events.Count > 0)
                    {
                        LogDecodedInput("VENDOR RX", bytesReturned, packet, events);
                        foreach (var inputEvent in events)
                        {
                            InputReceived?.Invoke(this, inputEvent);
                        }
                    }
                }

                await Task.Delay(VendorPollDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Vendor-driver poll loop failed");
                SetStatus(false, "Hercules vendor driver", $"Vendor-driver poll failed: {ex.Message}");
                return;
            }
        }
    }

    private void CollectWinUsbPipes(IntPtr interfaceHandle, string label, UsbInterfaceDescriptor descriptor, List<WinUsbReadTarget> readTargets, ref byte outPipe)
    {
        _logger.Info($"WinUSB interface {label}: number={descriptor.InterfaceNumber}, endpoints={descriptor.NumberOfEndpoints}, class=0x{descriptor.InterfaceClass:X2}, subclass=0x{descriptor.InterfaceSubClass:X2}, protocol=0x{descriptor.InterfaceProtocol:X2}");
        for (byte pipeIndex = 0; pipeIndex < descriptor.NumberOfEndpoints; pipeIndex++)
        {
            if (!WinUsb_QueryPipe(interfaceHandle, 0, pipeIndex, out var pipeInfo))
            {
                continue;
            }

            _logger.Info($"WinUSB pipe {label}/{pipeIndex}: type={pipeInfo.PipeType}, id=0x{pipeInfo.PipeId:X2}, maxPacket={pipeInfo.MaximumPacketSize}, interval={pipeInfo.Interval}");
            if ((pipeInfo.PipeId & 0x80) != 0)
            {
                readTargets.Add(new WinUsbReadTarget(label, interfaceHandle, pipeInfo.PipeId, Math.Max(64, (int)pipeInfo.MaximumPacketSize)));
            }
            else if (outPipe == 0)
            {
                outPipe = pipeInfo.PipeId;
            }
        }
    }

    private void WinUsbReadLoop(WinUsbReadTarget target, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var buffer = new byte[target.ReadSize];
            if (target.InterfaceHandle == IntPtr.Zero || target.PipeId == 0)
            {
                return;
            }

            if (!WinUsb_ReadPipe(target.InterfaceHandle, target.PipeId, buffer, buffer.Length, out var bytesRead, IntPtr.Zero))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                var error = Marshal.GetLastWin32Error();
                if (error == 121)
                {
                    continue;
                }

                var message = new Win32Exception(error).Message;
                _logger.Warn($"WinUSB read failed on {target.Label}/0x{target.PipeId:X2}: {message} ({error})");
                SetStatus(false, "WinUSB", $"WinUSB read failed: {message}");
                return;
            }

            if (bytesRead <= 0)
            {
                continue;
            }

            var packet = buffer.AsSpan(0, bytesRead).ToArray();
            var events = _decoder.Decode(packet);
            if (events.Count > 0)
            {
                LogDecodedInput($"WINUSB RX {target.Label}/0x{target.PipeId:X2}", bytesRead, packet, events);
                foreach (var inputEvent in events)
                {
                    InputReceived?.Invoke(this, inputEvent);
                }
            }
        }
    }

    private async Task<bool> ProbeReadSupportAsync(FileStream stream, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(200));
        var buffer = new byte[1];

        try
        {
            _ = await stream.ReadAsync(buffer.AsMemory(0, 1), timeout.Token).ConfigureAwait(false);
            _logger.Info("Device interface produced data during read probe.");
            return true;
        }
        catch (OperationCanceledException)
        {
            _logger.Info("Device interface read probe timed out without error; treating reads as supported.");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _logger.Warn($"Device interface read probe failed: {ex.Message}");
            return false;
        }
    }

    private void SetStatus(bool connected, string mode, string message)
    {
        Mode = mode;
        _logger.Info($"Hardware status: connected={connected}; mode={mode}; {message}");
        StatusChanged?.Invoke(this, new TransportStatus(connected, mode, message));
    }

    private void LogDecodedInput(string source, int bytesReturned, byte[] packet, IReadOnlyList<DeviceInputEvent> events)
    {
        if (events.Count == 0)
        {
            return;
        }

        if (IsEnvironmentEnabled("HWB_LOG_INPUT_PACKETS"))
        {
            _logger.Info($"{source} {bytesReturned} bytes: {Convert.ToHexString(packet)} -> {string.Join(", ", events)}");
            return;
        }

        if (events.Any(inputEvent => inputEvent is not KnobTurn))
        {
            _logger.Info($"{source}: {string.Join(", ", events)}");
        }
    }

    private static int GetIntEnvironment(string name, int fallback, int min, int max)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (int.TryParse(value, out var parsed))
        {
            return Math.Clamp(parsed, min, max);
        }

        return fallback;
    }

    private static bool IsEnvironmentEnabled(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return value is not null &&
               (value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("on", StringComparison.OrdinalIgnoreCase));
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        byte[]? lpInBuffer,
        int nInBufferSize,
        byte[]? lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_Initialize(SafeFileHandle deviceHandle, out IntPtr interfaceHandle);

    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_Free(IntPtr interfaceHandle);

    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_QueryInterfaceSettings(IntPtr interfaceHandle, byte alternateInterfaceNumber, out UsbInterfaceDescriptor usbAltInterfaceDescriptor);

    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_GetAssociatedInterface(IntPtr interfaceHandle, byte associatedInterfaceIndex, out IntPtr associatedInterfaceHandle);

    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_QueryPipe(IntPtr interfaceHandle, byte alternateInterfaceNumber, byte pipeIndex, out WinUsbPipeInformation pipeInformation);

    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_ReadPipe(IntPtr interfaceHandle, byte pipeId, byte[] buffer, int bufferLength, out int lengthTransferred, IntPtr overlapped);

    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_SetPipePolicy(IntPtr interfaceHandle, byte pipeId, uint policyType, uint valueLength, ref uint value);

    [StructLayout(LayoutKind.Sequential)]
    private struct UsbInterfaceDescriptor
    {
        public byte Length;
        public byte DescriptorType;
        public byte InterfaceNumber;
        public byte AlternateSetting;
        public byte NumberOfEndpoints;
        public byte InterfaceClass;
        public byte InterfaceSubClass;
        public byte InterfaceProtocol;
        public byte Interface;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinUsbPipeInformation
    {
        public int PipeType;
        public byte PipeId;
        public ushort MaximumPacketSize;
        public byte Interval;
    }

    private sealed record WinUsbReadTarget(string Label, IntPtr InterfaceHandle, byte PipeId, int ReadSize);

    private enum LcdMode
    {
        Auto,
        DynamicRaw,
        Helper,
        HelperBatch,
        PreviewOnly,
        Raw
    }
}
