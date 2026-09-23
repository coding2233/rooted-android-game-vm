using RootedAndroidGameVM.Core.Android;
using RootedAndroidGameVM.Core.Debugging;
using RootedAndroidGameVM.Core.Setup;
using RootedAndroidGameVM.Core.Storage;

namespace RootedAndroidGameVM.Core.Tests;

public sealed class InstallPathConfigurationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rgvm-path-config-tests", Guid.NewGuid().ToString("N"));
    private string ControlRoot => Path.Combine(_root, "control");
    private string DataRoot => Path.Combine(_root, "data");

    private async Task<InstallPathConfigurationService> CreateServiceAsync()
    {
        await new ProductStorageLocation(ControlRoot).SaveRootAsync(DataRoot, CancellationToken.None);
        return new InstallPathConfigurationService(ControlRoot);
    }

    private string CreateExternalSdk()
    {
        var sdk = Path.Combine(_root, "external-sdk");
        Directory.CreateDirectory(Path.Combine(sdk, "platform-tools"));
        Directory.CreateDirectory(Path.Combine(sdk, "emulator"));
        File.WriteAllText(Path.Combine(sdk, "platform-tools", "adb.exe"), string.Empty);
        File.WriteAllText(Path.Combine(sdk, "emulator", "emulator.exe"), string.Empty);
        return sdk;
    }

    [Fact]
    public async Task Missing_configuration_keeps_the_product_managed_defaults()
    {
        var service = await CreateServiceAsync();
        var paths = service.ResolvePaths();

        Assert.False(service.Read().HasOverrides);
        Assert.Equal(Path.Combine(DataRoot, "runtime", "android-sdk"), paths.SdkRoot);
        Assert.Equal(Path.Combine(DataRoot, "runtime", "avd"), paths.AvdHome);
        Assert.Equal(Path.Combine(DataRoot, "downloads"), paths.DownloadCache);
        Assert.False(paths.SdkIsExternal);
        Assert.False(paths.AvdIsExternal);
    }

    [Fact]
    public async Task External_sdk_is_reused_and_reported_as_external()
    {
        var service = await CreateServiceAsync();
        var sdk = CreateExternalSdk();

        var change = await service.ConfigureAsync(sdk, null, null);

        Assert.Equal(sdk, change.Paths.SdkRoot);
        Assert.True(change.Paths.SdkIsExternal);
        Assert.True(change.NextStartRequired);
        Assert.Equal(sdk, service.Read().SdkRoot);
        // An SDK without cmdline-tools is accepted; the installer adds them before creating the AVD.
        Assert.Null(AndroidSdkLayout.FromRoot(sdk).FindCommandLineToolsBin());
    }

    [Fact]
    public async Task Avd_and_download_cache_can_be_placed_outside_the_resource_root()
    {
        var service = await CreateServiceAsync();
        var avd = Path.Combine(_root, "external-avd");
        var cache = Path.Combine(_root, "external-cache");

        var change = await service.ConfigureAsync(null, avd, cache);

        Assert.Equal(avd, change.Paths.AvdHome);
        Assert.True(change.Paths.AvdIsExternal);
        Assert.Equal(cache, change.Paths.DownloadCache);
        Assert.False(change.Paths.SdkIsExternal);
    }

    [Fact]
    public async Task Configuration_survives_a_new_store_instance()
    {
        var service = await CreateServiceAsync();
        var sdk = CreateExternalSdk();
        await service.ConfigureAsync(sdk, null, null);

        var reloaded = new InstallPathConfigurationService(ControlRoot).Read();

        Assert.Equal(sdk, reloaded.SdkRoot);
        Assert.True(File.Exists(Path.Combine(ControlRoot, InstallPathConfigurationStore.FileName)));
    }

    [Fact]
    public async Task Explicit_null_restores_the_product_managed_default()
    {
        var service = await CreateServiceAsync();
        await service.ConfigureAsync(CreateExternalSdk(), null, null);

        var change = await service.ConfigureAsync(null, null, null);

        Assert.Null(change.Configuration.SdkRoot);
        Assert.Equal(Path.Combine(DataRoot, "runtime", "android-sdk"), change.Paths.SdkRoot);
    }

    [Fact]
    public async Task Corrupt_configuration_fails_instead_of_silently_using_defaults()
    {
        await CreateServiceAsync();
        Directory.CreateDirectory(ControlRoot);
        File.WriteAllText(Path.Combine(ControlRoot, InstallPathConfigurationStore.FileName), "invalid-json");
        var store = new InstallPathConfigurationStore(ControlRoot);

        Assert.Throws<InvalidDataException>(() => store.Read());
    }

    [Fact]
    public void Relative_or_drive_root_paths_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => InstallPathConfiguration.Create("relative"));
        Assert.Throws<ArgumentException>(() => InstallPathConfiguration.Create(Path.GetPathRoot(_root)!));
    }

    [Fact]
    public async Task Configuring_a_missing_sdk_directory_fails_closed()
    {
        var service = await CreateServiceAsync();
        var missing = Path.Combine(_root, "no-such-sdk");

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => service.ConfigureAsync(missing, null, null));
        Assert.False(File.Exists(Path.Combine(ControlRoot, InstallPathConfigurationStore.FileName)));
    }

    [Fact]
    public async Task Configuring_an_sdk_without_platform_tools_fails_closed()
    {
        var service = await CreateServiceAsync();
        var emptySdk = Path.Combine(_root, "empty-sdk");
        Directory.CreateDirectory(emptySdk);

        await Assert.ThrowsAsync<FileNotFoundException>(() => service.ConfigureAsync(emptySdk, null, null));
    }

    [Fact]
    public async Task Configuring_a_folder_that_contains_the_control_directory_is_rejected()
    {
        var service = await CreateServiceAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ConfigureAsync(null, _root, null));
    }

    [Fact]
    public async Task Clear_removes_the_override_file()
    {
        var service = await CreateServiceAsync();
        await service.ConfigureAsync(CreateExternalSdk(), null, null);
        Assert.True(File.Exists(Path.Combine(ControlRoot, InstallPathConfigurationStore.FileName)));

        service.Clear();

        Assert.False(File.Exists(Path.Combine(ControlRoot, InstallPathConfigurationStore.FileName)));
        Assert.False(service.Read().HasOverrides);
    }

    [Fact]
    public void Command_line_tools_bin_is_discovered_for_versioned_layouts()
    {
        var sdk = Path.Combine(_root, "versioned-sdk");
        var versioned = Path.Combine(sdk, "cmdline-tools", "16.0", "bin");
        Directory.CreateDirectory(versioned);
        File.WriteAllText(Path.Combine(versioned, "avdmanager.bat"), string.Empty);

        var layout = AndroidSdkLayout.FromRoot(sdk);

        Assert.Equal(versioned, layout.FindCommandLineToolsBin());
    }

    [Fact]
    public void Command_line_tools_bin_prefers_latest()
    {
        var sdk = Path.Combine(_root, "latest-sdk");
        var latest = Path.Combine(sdk, "cmdline-tools", "latest", "bin");
        var versioned = Path.Combine(sdk, "cmdline-tools", "16.0", "bin");
        Directory.CreateDirectory(latest);
        Directory.CreateDirectory(versioned);
        File.WriteAllText(Path.Combine(latest, "avdmanager.bat"), string.Empty);
        File.WriteAllText(Path.Combine(versioned, "avdmanager.bat"), string.Empty);

        var layout = AndroidSdkLayout.FromRoot(sdk);

        Assert.Equal(latest, layout.FindCommandLineToolsBin());
    }

    [Fact]
    public void Path_commands_are_exposed_to_gui_and_cli()
    {
        var names = DebugCommandCatalog.Commands.Select(command => command.Name).ToArray();

        Assert.Contains("paths.inspect", names);
        Assert.Contains("paths.configure", names);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
