using RootedAndroidGameVM.Core.Dependencies;
using RootedAndroidGameVM.Core.Downloads;
using RootedAndroidGameVM.Core.Security;

namespace RootedAndroidGameVM.Core.Setup;

/// <summary>One component the installer must have locally before it can install offline.</summary>
public sealed record DependencyDownloadItem(
    string Id,
    string Name,
    string Version,
    string Url,
    string ArchiveFileName,
    string Sha256,
    long Size);

public sealed record DependencyDownloadState(
    DependencyDownloadItem Item,
    bool Verified,
    long BytesOnDisk,
    bool PartialPresent,
    string EffectiveUrl);

/// <summary>
/// Lists the pinned components the installer downloads and reports which of them are already
/// present and verified in the download cache. A file is recognized by content (size + SHA-256),
/// not only by its exact file name, so a manual download with the URL's own name still counts.
/// </summary>
public static class DependencyDownloadCatalog
{
    public static IReadOnlyList<DependencyDownloadItem> Required() =>
        DependencyManifest.LoadEmbedded().Components
            .Where(component => component.DownloadAtInstall)
            .Select(component => new DependencyDownloadItem(
                component.Id,
                component.Name,
                component.Version,
                component.Url,
                component.ArchiveFileName,
                component.Sha256,
                component.Size))
            .ToArray();

    public static async Task<IReadOnlyList<DependencyDownloadState>> InspectAsync(
        string cacheDirectory,
        DownloadSourceConfiguration? configuration = null,
        IReadOnlyList<DependencyDownloadItem>? items = null,
        CancellationToken cancellationToken = default)
    {
        configuration ??= DownloadSourceConfiguration.Empty;
        var list = items ?? Required();
        var hashes = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var results = new List<DependencyDownloadState>(list.Count);
        foreach (var item in list)
        {
            var exactPath = Path.Combine(cacheDirectory, item.ArchiveFileName);
            var (verified, bytes) = await VerifyAsync(exactPath, item, hashes, cancellationToken);
            if (!verified)
            {
                var match = await FindByContentAsync(cacheDirectory, item, hashes, cancellationToken);
                if (match is not null)
                {
                    verified = true;
                    bytes = new FileInfo(match).Length;
                }
            }
            results.Add(new(
                item,
                verified,
                bytes,
                File.Exists(exactPath + ".partial"),
                configuration.Rewrite(new Uri(item.Url)).ToString()));
        }
        return results;
    }

    /// <summary>
    /// Renames any content-verified cache file to the exact file name the installer expects.
    /// This makes a manual download usable even when the URL's file name differs from the
    /// pinned archive name. Returns the archive names that were adopted.
    /// </summary>
    public static async Task<IReadOnlyList<string>> AdoptAllAsync(
        string cacheDirectory,
        IReadOnlyList<DependencyDownloadItem>? items = null,
        CancellationToken cancellationToken = default)
    {
        var list = items ?? Required();
        var adopted = new List<string>();
        if (!Directory.Exists(cacheDirectory)) return adopted;
        var hashes = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in list)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var exactPath = Path.Combine(cacheDirectory, item.ArchiveFileName);
            if (File.Exists(exactPath) && (await VerifyAsync(exactPath, item, hashes, cancellationToken)).Verified)
                continue;
            var match = await FindByContentAsync(cacheDirectory, item, hashes, cancellationToken);
            if (match is null || string.Equals(match, exactPath, StringComparison.OrdinalIgnoreCase)) continue;
            File.Move(match, exactPath, overwrite: true);
            hashes.Remove(exactPath);
            adopted.Add(item.ArchiveFileName);
        }
        return adopted;
    }

    private static async Task<(bool Verified, long Bytes)> VerifyAsync(
        string path,
        DependencyDownloadItem item,
        Dictionary<string, string?> hashes,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return (false, 0);
        var length = new FileInfo(path).Length;
        if (item.Size > 0 && length != item.Size) return (false, length);
        return (string.Equals(await HashAsync(path, hashes, cancellationToken), item.Sha256,
            StringComparison.OrdinalIgnoreCase), length);
    }

    private static async Task<string?> FindByContentAsync(
        string cacheDirectory,
        DependencyDownloadItem item,
        Dictionary<string, string?> hashes,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(cacheDirectory)) return null;
        foreach (var file in Directory.EnumerateFiles(cacheDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(file);
            if (name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".importing", StringComparison.OrdinalIgnoreCase)) continue;
            if (item.Size > 0 && new FileInfo(file).Length != item.Size) continue;
            if (string.Equals(await HashAsync(file, hashes, cancellationToken), item.Sha256,
                    StringComparison.OrdinalIgnoreCase)) return file;
        }
        return null;
    }

    private static async Task<string?> HashAsync(
        string path,
        Dictionary<string, string?> hashes,
        CancellationToken cancellationToken)
    {
        if (hashes.TryGetValue(path, out var cached)) return cached;
        string? hash;
        try { hash = await Sha256Verifier.ComputeAsync(path, cancellationToken); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { hash = null; }
        hashes[path] = hash;
        return hash;
    }
}
