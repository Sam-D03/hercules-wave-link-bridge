using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace HerculesWaveBridge;

internal sealed class NativeHsmSdk : IDisposable
{
    public const int ScreenWidth = 480;
    public const int ScreenHeight = 272;
    public const int StripWidth = 110;
    public const int StripHeight = 16;

    private const uint PageExecuteReadWrite = 0x40;
    private const uint CustomBackgroundSelector = 0xFF;

    private static readonly byte[] ScreenConfig40 = Convert.FromHexString(
        "28000000" + "00000000" + "01000000" + "03000000" +
        "ffffffff" + "ffffffff" + "05000000" + "ffffffff" +
        "01000000" + "00000000");

    private static readonly byte[] DisplaySettings16 = Convert.FromHexString(
        "10000000" + "00000040" + "d964e33f" + "00020000");

    private readonly BridgeLogger _logger;
    private readonly string _sdkDirectory;
    private readonly string _hsmDllPath;
    private readonly string _tlusbDllPath;
    private readonly IntPtr _library;
    private readonly IntPtr _callbackContext;
    private readonly HsmCallback _callback;
    private readonly List<Delegate> _redirectHooks = [];
    private int _handle;
    private bool _disposed;

    private readonly HsmInit _hsmInit;
    private readonly HsmIsDeviceChanged _hsmIsDeviceChanged;
    private readonly HsmGetDeviceCount _hsmGetDeviceCount;
    private readonly HsmOpenDevice _hsmOpenDevice;
    private readonly HsmCloseDevice _hsmCloseDevice;
    private readonly HsmSetScreenConfiguration _hsmSetScreenConfiguration;
    private readonly HsmSetDisplaySettings _hsmSetDisplaySettings;
    private readonly HsmPrepareGraphicElement _hsmPrepareGraphicElement;
    private readonly HsmSetGraphicElement _hsmSetGraphicElement;
    private readonly HsmUnprepareGraphicElement _hsmUnprepareGraphicElement;
    private readonly HsmGetDeviceScreenContent? _hsmGetDeviceScreenContent;
    private readonly HsmSetAudioLevels? _hsmSetAudioLevels;
    private readonly HsmSetAudioChannelStyle? _hsmSetAudioChannelStyle;
    private readonly HsmSetLedStyle? _hsmSetLedStyle;

    public NativeHsmSdk(string sdkDirectory, BridgeLogger logger)
    {
        _logger = logger;
        _sdkDirectory = sdkDirectory;
        _hsmDllPath = Path.Combine(_sdkDirectory, "hsm_api_core_x64.dll");
        _tlusbDllPath = Path.Combine(_sdkDirectory, "tlusbapi_x64.dll");
        if (!File.Exists(_hsmDllPath))
        {
            throw new FileNotFoundException("Packaged Hercules HSM SDK DLL was not found.", _hsmDllPath);
        }

        if (!File.Exists(_tlusbDllPath))
        {
            throw new FileNotFoundException("Packaged Hercules USB API DLL was not found.", _tlusbDllPath);
        }

        SetDllDirectoryW(_sdkDirectory);
        _library = NativeLibrary.Load(_hsmDllPath);
        InstallTlusbRedirect();

        _callbackContext = Marshal.AllocHGlobal(4096);
        ZeroMemory(_callbackContext, 4096);
        _callback = (_, _, _, _) => 0;

        _hsmInit = GetExport<HsmInit>("HSM_Init");
        _hsmIsDeviceChanged = GetExport<HsmIsDeviceChanged>("HSM_IsDeviceChanged");
        _hsmGetDeviceCount = GetExport<HsmGetDeviceCount>("HSM_GetDeviceCount");
        _hsmOpenDevice = GetExport<HsmOpenDevice>("HSM_OpenDevice");
        _hsmCloseDevice = GetExport<HsmCloseDevice>("HSM_CloseDevice");
        _hsmSetScreenConfiguration = GetExport<HsmSetScreenConfiguration>("HSM_SetScreenConfiguration");
        _hsmSetDisplaySettings = GetExport<HsmSetDisplaySettings>("HSM_SetDisplaySettings");
        _hsmPrepareGraphicElement = GetExport<HsmPrepareGraphicElement>("HSM_PrepareGraphicElement");
        _hsmSetGraphicElement = GetExport<HsmSetGraphicElement>("HSM_SetGraphicElement");
        _hsmUnprepareGraphicElement = GetExport<HsmUnprepareGraphicElement>("HSM_UnPrepareGraphicElement");
        _hsmGetDeviceScreenContent = TryGetExport<HsmGetDeviceScreenContent>("HSM_GetDeviceScreenContent");
        _hsmSetAudioLevels = TryGetExport<HsmSetAudioLevels>("HSM_SetAudioLevels");
        _hsmSetAudioChannelStyle = TryGetExport<HsmSetAudioChannelStyle>("HSM_SetAudioChannelStyle");
        _hsmSetLedStyle = TryGetExport<HsmSetLedStyle>("HSM_SetLedStyle");
    }

    public int Handle => _handle;

    public int InitWait(TimeSpan timeout, CancellationToken cancellationToken)
    {
        _hsmInit();
        var deadline = DateTimeOffset.Now + timeout;
        while (DateTimeOffset.Now < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _hsmIsDeviceChanged();
            var count = GetDeviceCount();
            if (count > 0)
            {
                return count;
            }

            Thread.Sleep(100);
        }

        return GetDeviceCount();
    }

    public int Open(int index = 0)
    {
        var output = Marshal.AllocHGlobal(8192);
        try
        {
            ZeroMemory(output, 8192);
            var callbackPtr = Marshal.GetFunctionPointerForDelegate(_callback);
            var result = _hsmOpenDevice(index, output, callbackPtr, _callbackContext);
            _handle = Marshal.ReadInt32(output);
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(output);
        }
    }

    public int SetScreenConfiguration() => SetScreenConfiguration(ScreenConfig40);

    public int SetCustomBackground(int elementId)
    {
        var config = ScreenConfig40.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(config.AsSpan(8, 4), CustomBackgroundSelector);
        BinaryPrimitives.WriteInt32LittleEndian(config.AsSpan(12, 4), elementId);
        return SetScreenConfiguration(config);
    }

    public int SetDisplay() => WithPinned(DisplaySettings16, pointer => _hsmSetDisplaySettings(_handle, pointer));

    public int PrepareGraphicElement(int kind, int width, int height, byte[] pixels)
    {
        var expected = checked(width * height * 4);
        if (pixels.Length != expected)
        {
            throw new ArgumentException($"Graphic payload is {pixels.Length} bytes; expected {expected}.", nameof(pixels));
        }

        return WithPinned(pixels, pointer =>
        {
            var result = _hsmPrepareGraphicElement(kind, width, height, pointer, out var id);
            if (result != 0)
            {
                throw new InvalidOperationException($"HSM_PrepareGraphicElement(kind={kind}, {width}x{height}) failed with {result}.");
            }

            return id;
        });
    }

    public int SetGraphicElement(uint target, int preparedId, int p4 = 0) =>
        _hsmSetGraphicElement(_handle, target, preparedId, p4);

    public int UnprepareGraphicElement(int preparedId) =>
        _hsmUnprepareGraphicElement(preparedId);

    public int SetAudioChannelStyle(int channel, byte[] style)
    {
        if (_hsmSetAudioChannelStyle is null)
        {
            return -1;
        }

        return WithPinned(style, pointer => _hsmSetAudioChannelStyle(_handle, channel, pointer));
    }

    public int SetAudioLevels6(int channel, double a0, double a1, double a2, double b0, double b1, double b2)
    {
        if (_hsmSetAudioLevels is null)
        {
            return -1;
        }

        var data = new byte[0x38];
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(0, 4), 0x38);
        WriteDouble(data, 4, a0);
        WriteDouble(data, 12, a1);
        WriteDouble(data, 20, a2);
        WriteDouble(data, 28, b0);
        WriteDouble(data, 36, b1);
        WriteDouble(data, 44, b2);
        return WithPinned(data, pointer => _hsmSetAudioLevels(_handle, channel, pointer));
    }

    public int SetLedStyle(int index, int valueA, int? valueB = null) =>
        _hsmSetLedStyle?.Invoke(_handle, index, valueA, valueB ?? valueA) ?? -1;

    public byte[]? GetScreenContent()
    {
        if (_hsmGetDeviceScreenContent is null)
        {
            return null;
        }

        var buffer = new byte[ScreenWidth * ScreenHeight * 4];
        var result = WithPinned(buffer, pointer => _hsmGetDeviceScreenContent(_handle, pointer, buffer.Length));
        return result == 0 ? buffer : null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_handle != 0)
        {
            try
            {
                _hsmCloseDevice(_handle);
            }
            catch
            {
                // Native shutdown is best-effort.
            }
        }

        if (_callbackContext != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_callbackContext);
        }

        if (_library != IntPtr.Zero)
        {
            try
            {
                NativeLibrary.Free(_library);
            }
            catch
            {
                // Let process teardown reclaim it if the SDK refuses to unload.
            }
        }
    }

    private int GetDeviceCount()
    {
        var result = _hsmGetDeviceCount(out var count);
        return result == 0 ? count : 0;
    }

    private int SetScreenConfiguration(byte[] config) =>
        WithPinned(config, pointer => _hsmSetScreenConfiguration(_handle, pointer));

    private T GetExport<T>(string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_library, name));

    private T? TryGetExport<T>(string name) where T : Delegate
    {
        try
        {
            return GetExport<T>(name);
        }
        catch
        {
            return null;
        }
    }

    private void InstallTlusbRedirect()
    {
        if (IsEnvironmentEnabled("HWB_DISABLE_TLUSB_REDIRECT"))
        {
            return;
        }

        try
        {
            var loadLibraryHook = new LoadLibraryWDelegate(LoadLibraryWRedirect);
            var loadLibraryExHook = new LoadLibraryExWDelegate(LoadLibraryExWRedirect);
            var callbacks = new Dictionary<string, Delegate>(StringComparer.Ordinal)
            {
                ["LoadLibraryW"] = loadLibraryHook,
                ["LoadLibraryExW"] = loadLibraryExHook
            };

            var slots = FindImportAddressTableSlots(_hsmDllPath, callbacks.Keys);
            var patched = 0;
            foreach (var (name, callback) in callbacks)
            {
                if (!slots.TryGetValue(name, out var slotRva))
                {
                    continue;
                }

                PatchPointer(_library + (nint)slotRva, Marshal.GetFunctionPointerForDelegate(callback));
                _redirectHooks.Add(callback);
                patched++;
            }

            _logger.Info($"Installed Hercules TLUSB redirect hook: patched {patched} loader import(s); sdk={_sdkDirectory}.");
        }
        catch (Exception ex)
        {
            _logger.Warn($"Could not install Hercules TLUSB redirect hook; SDK may use installed Program Files runtime if present: {ex.Message}");
        }
    }

    private IntPtr LoadLibraryWRedirect(string? path) =>
        ShouldRedirectDll(path) ? LoadLibraryW(_tlusbDllPath) : LoadLibraryW(path);

    private IntPtr LoadLibraryExWRedirect(string? path, IntPtr fileHandle, uint flags) =>
        ShouldRedirectDll(path) ? LoadLibraryExW(_tlusbDllPath, fileHandle, flags) : LoadLibraryExW(path, fileHandle, flags);

    private static bool ShouldRedirectDll(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        path.Replace('/', '\\').EndsWith("tlusbapi_x64.dll", StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, uint> FindImportAddressTableSlots(string modulePath, IEnumerable<string> functionNames)
    {
        var data = File.ReadAllBytes(modulePath);
        var wanted = functionNames.ToHashSet(StringComparer.Ordinal);
        var pe = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0x3C, 4));
        var optionalHeader = pe + 24;
        var magic = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(optionalHeader, 2));
        int dataDirectory;
        ulong ordinalMask;
        int thunkSize;
        if (magic == 0x20B)
        {
            dataDirectory = optionalHeader + 112;
            ordinalMask = 0x8000000000000000UL;
            thunkSize = 8;
        }
        else if (magic == 0x10B)
        {
            dataDirectory = optionalHeader + 96;
            ordinalMask = 0x80000000UL;
            thunkSize = 4;
        }
        else
        {
            throw new InvalidDataException($"Unsupported PE optional header magic 0x{magic:x}.");
        }

        var sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pe + 6, 2));
        var sectionOffset = optionalHeader + BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pe + 20, 2));
        var sections = new List<PeSection>();
        for (var index = 0; index < sectionCount; index++)
        {
            var offset = sectionOffset + index * 40;
            var virtualSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 8, 4));
            var virtualAddress = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 12, 4));
            var rawSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 16, 4));
            var rawAddress = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 20, 4));
            sections.Add(new PeSection(virtualAddress, Math.Max(virtualSize, rawSize), rawAddress));
        }

        var importRva = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(dataDirectory + 8, 4));
        if (importRva == 0)
        {
            return [];
        }

        var importOffset = RvaToOffset(importRva, sections)
            ?? throw new InvalidDataException("Could not map import directory RVA.");
        var importOffsetValue = checked((int)importOffset);

        var slots = new Dictionary<string, uint>(StringComparer.Ordinal);
        for (var descriptorIndex = 0;; descriptorIndex++)
        {
            var descriptor = importOffsetValue + descriptorIndex * 20;
            var originalFirstThunk = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(descriptor, 4));
            var nameRva = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(descriptor + 12, 4));
            var firstThunk = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(descriptor + 16, 4));
            if (originalFirstThunk == 0 && nameRva == 0 && firstThunk == 0)
            {
                break;
            }

            var dllNameOffset = RvaToOffset(nameRva, sections);
            var dllName = dllNameOffset is null ? string.Empty : ReadNullTerminatedAscii(data, dllNameOffset.Value);
            if (!dllName.Equals("kernel32.dll", StringComparison.OrdinalIgnoreCase) &&
                !dllName.Equals("api-ms-win-core-libraryloader-l1-2-0.dll", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var lookupRva = originalFirstThunk == 0 ? firstThunk : originalFirstThunk;
            var lookupOffset = RvaToOffset(lookupRva, sections);
            if (lookupOffset is null)
            {
                continue;
            }

            var lookupOffsetValue = checked((int)lookupOffset.Value);
            for (var thunkIndex = 0;; thunkIndex++)
            {
                var thunkValue = thunkSize == 8
                    ? BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(lookupOffsetValue + thunkIndex * thunkSize, 8))
                    : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(lookupOffsetValue + thunkIndex * thunkSize, 4));
                if (thunkValue == 0)
                {
                    break;
                }

                if ((thunkValue & ordinalMask) != 0)
                {
                    continue;
                }

                var importNameOffset = RvaToOffset((uint)thunkValue, sections);
                if (importNameOffset is null)
                {
                    continue;
                }

                var importName = ReadNullTerminatedAscii(data, importNameOffset.Value + 2);
                if (wanted.Contains(importName))
                {
                    slots[importName] = firstThunk + (uint)(thunkIndex * thunkSize);
                }
            }
        }

        return slots;
    }

    private static uint? RvaToOffset(uint rva, IReadOnlyList<PeSection> sections)
    {
        foreach (var section in sections)
        {
            if (rva >= section.VirtualAddress && rva < section.VirtualAddress + section.Size)
            {
                return section.RawAddress + (rva - section.VirtualAddress);
            }
        }

        return null;
    }

    private static string ReadNullTerminatedAscii(byte[] data, uint offset)
    {
        var end = (int)offset;
        while (end < data.Length && data[end] != 0)
        {
            end++;
        }

        return System.Text.Encoding.ASCII.GetString(data, (int)offset, end - (int)offset);
    }

    private static void PatchPointer(IntPtr address, IntPtr replacement)
    {
        if (!VirtualProtect(address, (nuint)IntPtr.Size, PageExecuteReadWrite, out var oldProtection))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            Marshal.WriteIntPtr(address, replacement);
        }
        finally
        {
            VirtualProtect(address, (nuint)IntPtr.Size, oldProtection, out _);
        }
    }

    private static void WriteDouble(byte[] data, int offset, double value) =>
        BitConverter.GetBytes(value).CopyTo(data, offset);

    private static int WithPinned(byte[] bytes, Func<IntPtr, int> action)
    {
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            return action(handle.AddrOfPinnedObject());
        }
        finally
        {
            handle.Free();
        }
    }

    private static T WithPinned<T>(byte[] bytes, Func<IntPtr, T> action)
    {
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            return action(handle.AddrOfPinnedObject());
        }
        finally
        {
            handle.Free();
        }
    }

    private static void ZeroMemory(IntPtr address, int length)
    {
        var zero = new byte[length];
        Marshal.Copy(zero, 0, address, length);
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

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetDllDirectoryW(string? pathName);

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibraryW(string? fileName);

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibraryExW(string? fileName, IntPtr fileHandle, uint flags);

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool VirtualProtect(IntPtr address, nuint size, uint newProtect, out uint oldProtect);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int HsmInit();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int HsmIsDeviceChanged();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int HsmGetDeviceCount(out int count);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int HsmOpenDevice(int index, IntPtr output, IntPtr callback, IntPtr context);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int HsmCloseDevice(int handle);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int HsmSetScreenConfiguration(int handle, IntPtr config);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int HsmSetDisplaySettings(int handle, IntPtr config);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int HsmPrepareGraphicElement(int kind, int width, int height, IntPtr pixels, out int elementId);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int HsmSetGraphicElement(int handle, uint target, int preparedId, int p4);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int HsmUnprepareGraphicElement(int preparedId);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int HsmGetDeviceScreenContent(int handle, IntPtr buffer, int bufferSize);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int HsmSetAudioLevels(int handle, int channel, IntPtr levels);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int HsmSetAudioChannelStyle(int handle, int channel, IntPtr style);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int HsmSetLedStyle(int handle, int index, int valueA, int valueB);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int HsmCallback(IntPtr a, IntPtr b, IntPtr c, IntPtr d);

    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode)]
    private delegate IntPtr LoadLibraryWDelegate(string? fileName);

    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode)]
    private delegate IntPtr LoadLibraryExWDelegate(string? fileName, IntPtr fileHandle, uint flags);

    private sealed record PeSection(uint VirtualAddress, uint Size, uint RawAddress);
}
