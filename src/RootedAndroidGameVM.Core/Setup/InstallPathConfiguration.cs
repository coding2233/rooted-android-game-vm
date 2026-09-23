using RootedAndroidGameVM.Core.Storage;

namespace RootedAndroidGameVM.Core.Setup;

/// <summary>
/// Optional overrides for where the product keeps or reuses Android components.
/// A null value means "keep the product-managed default under the resource root".
/// A non-null value is an explicit local folder chosen by the user.
/// </summary>
public sealed record InstallPathConfiguration(int SchemaVersion, string? SdkRoot, string? AvdHome, string? DownloadCache)
{
    public const int CurrentSchemaVersion = 1;

    public static InstallPathConfiguration Empty { get; } = new(CurrentSchemaVersion, null, null, null);

    public bool HasOverrides => SdkRoot is not null || AvdHome is not null || DownloadCache is not null;

    public static InstallPathConfiguration Create(string? sdkRoot = null, string? avdHome = null, string? downloadCache = null) =>
        new(CurrentSchemaVersion,
            Normalize(sdkRoot),
            Normalize(avdHome),
            Normalize(downloadCache));

    private static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var normalized = StoragePathPolicy.NormalizeRoot(path);
        StoragePathPolicy.RejectReparsePoints(normalized);
        return normalized;
    }
}
