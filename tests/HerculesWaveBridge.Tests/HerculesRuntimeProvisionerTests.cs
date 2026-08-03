using Xunit;

namespace HerculesWaveBridge.Tests;

public sealed class HerculesRuntimeProvisionerTests : IDisposable
{
    private static readonly string[] RequiredFiles =
    [
        "hsm_api_core_x64.dll",
        "tlusbapi_x64.dll",
        "tlusbapi_x64.dll.config.ini",
        "tlusbapi_x64.dll.license.ini",
        "HSM01_S32L4R7_v1_38.hsm",
        "HSM01_S32L4R7_v1_42.hsm"
    ];

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"hwb-runtime-test-{Guid.NewGuid():N}");
    private readonly string? _previousSdkPath = Environment.GetEnvironmentVariable("HWB_HERCULES_SDK_DIR");

    [Fact]
    public void ProvisionsRuntimeFromUsersInstalledSdkDirectory()
    {
        var source = Path.Combine(_root, "sdk");
        var destination = Path.Combine(_root, "display");
        Directory.CreateDirectory(source);
        foreach (var fileName in RequiredFiles)
        {
            File.WriteAllText(Path.Combine(source, fileName), fileName);
        }

        Environment.SetEnvironmentVariable("HWB_HERCULES_SDK_DIR", source);
        var success = HerculesRuntimeProvisioner.TryProvision(
            destination,
            BridgeLogger.CreateDefault(),
            out var error);

        Assert.True(success, error);
        Assert.Equal(Path.GetFullPath(source), HerculesRuntimeProvisioner.TryFindSdkDirectory());
        foreach (var fileName in RequiredFiles)
        {
            Assert.Equal(fileName, File.ReadAllText(Path.Combine(destination, fileName)));
        }
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("HWB_HERCULES_SDK_DIR", _previousSdkPath);
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
