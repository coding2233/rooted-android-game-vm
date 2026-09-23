using RootedAndroidGameVM.Core.Android;
using RootedAndroidGameVM.Core.Storage;

namespace RootedAndroidGameVM.Core.Setup;

public sealed record InstallPathChangeResult(
    InstallPathConfiguration Configuration,
    InstallPaths Paths,
    bool NextStartRequired);

/// <summary>
/// Validates and persists SDK/AVD/download-cache overrides, then resolves the effective
/// <see cref="InstallPaths"/>. External folders are verified but never silently created
/// or modified by this service.
/// </summary>
public sealed class InstallPathConfigurationService
{
    private readonly InstallPathConfigurationStore _store;
    private readonly ProductStorageLocation _location;

    public InstallPathConfigurationService(string? controlRoot = null)
    {
        _store = new InstallPathConfigurationStore(controlRoot);
        _location = new ProductStorageLocation(controlRoot);
    }

    public string ConfigurationPath => _store.FilePath;

    public InstallPathConfiguration Read() => _store.Read();

    public InstallPaths ResolvePaths() => InstallPaths.FromProductRoot(_location.ReadRoot(), _store.Read());

    public async Task<InstallPathChangeResult> ConfigureAsync(
        string? sdkRoot,
        string? avdHome,
        string? downloadCache,
        CancellationToken cancellationToken = default)
    {
        var configuration = InstallPathConfiguration.Create(sdkRoot, avdHome, downloadCache);
        Validate(configuration);
        await _store.SaveAsync(configuration, cancellationToken);
        return new(configuration, ResolvePaths(), NextStartRequired: true);
    }

    public void Clear() => _store.Clear();

    private void Validate(InstallPathConfiguration configuration)
    {
        if (configuration.SdkRoot is { } sdkRoot)
        {
            StorageOwnership.AssertSafeRoot(sdkRoot, _store.ControlRoot);
            if (!Directory.Exists(sdkRoot))
                throw new DirectoryNotFoundException($"SDK 目录不存在：{sdkRoot}。请选择已安装的 Android SDK。");
            var layout = AndroidSdkLayout.FromRoot(sdkRoot);
            if (!File.Exists(layout.AdbPath))
                throw new FileNotFoundException($"SDK 目录缺少 platform-tools\\adb.exe：{sdkRoot}。", layout.AdbPath);
            if (!File.Exists(layout.EmulatorPath))
                throw new FileNotFoundException($"SDK 目录缺少 emulator\\emulator.exe：{sdkRoot}。", layout.EmulatorPath);
        }

        if (configuration.AvdHome is { } avdHome)
        {
            StorageOwnership.AssertSafeRoot(avdHome, _store.ControlRoot);
        }

        if (configuration.DownloadCache is { } downloadCache)
        {
            StorageOwnership.AssertSafeRoot(downloadCache, _store.ControlRoot);
        }
    }
}
