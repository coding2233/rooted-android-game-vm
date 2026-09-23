using RootedAndroidGameVM.Core.IO;
using RootedAndroidGameVM.Core.Storage;

namespace RootedAndroidGameVM.Core.Setup;

public sealed record InstallPaths(
    string ProductRoot,
    string RuntimeRoot,
    string SdkRoot,
    string JavaHome,
    string AvdHome,
    string RootAvdRoot,
    string DownloadCache)
{
    /// <summary>True when the SDK lives outside the product resource root (reused, verify-only).</summary>
    public bool SdkIsExternal => !StoragePathPolicy.Contains(ProductRoot, SdkRoot);

    /// <summary>True when the AVD home lives outside the product resource root.</summary>
    public bool AvdIsExternal => !StoragePathPolicy.Contains(ProductRoot, AvdHome);

    public static InstallPaths CreateDefault(string? controlRoot = null) =>
        FromProductRoot(
            new ProductStorageLocation(controlRoot).ReadRoot(),
            new InstallPathConfigurationStore(controlRoot).Read());

    public static InstallPaths FromProductRoot(string productRoot, InstallPathConfiguration? configuration = null)
    {
        var root = Path.GetFullPath(productRoot);
        var config = configuration ?? InstallPathConfiguration.Empty;
        var runtime = PathBoundary.EnsureWithinRoot(root, Path.Combine(root, "runtime"));
        return new(
            root,
            runtime,
            config.SdkRoot ?? PathBoundary.EnsureWithinRoot(root, Path.Combine(runtime, "android-sdk")),
            PathBoundary.EnsureWithinRoot(root, Path.Combine(runtime, "java")),
            config.AvdHome ?? PathBoundary.EnsureWithinRoot(root, Path.Combine(runtime, "avd")),
            PathBoundary.EnsureWithinRoot(root, Path.Combine(runtime, "rootavd")),
            config.DownloadCache ?? PathBoundary.EnsureWithinRoot(root, Path.Combine(root, "downloads")));
    }
}
