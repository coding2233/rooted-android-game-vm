using RootedAndroidGameVM.Core.Security;

namespace RootedAndroidGameVM.Core.Setup;

/// <summary>Outcome of importing one component from a user-provided folder.</summary>
public sealed record DependencyImportResult(string Id, string ArchiveFileName, string Outcome, string? Detail);

/// <summary>
/// Imports already-downloaded pinned archives from a local folder into the download cache.
/// Only files whose SHA-256 matches the pinned digest are accepted, so a mirror or a manual
/// download can never substitute different content.
/// </summary>
public sealed class DependencyImportService(IReadOnlyList<DependencyDownloadItem>? items = null)
{
    private readonly IReadOnlyList<DependencyDownloadItem> _items = items ?? DependencyDownloadCatalog.Required();

    public async Task<IReadOnlyList<DependencyImportResult>> ImportAsync(
        string sourceFolder,
        string cacheDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFolder);
        if (!Directory.Exists(sourceFolder))
            throw new DirectoryNotFoundException($"导入文件夹不存在：{sourceFolder}。");
        Directory.CreateDirectory(cacheDirectory);

        var files = Directory.EnumerateFiles(sourceFolder, "*", SearchOption.AllDirectories).ToArray();
        var hashes = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        async Task<string?> HashAsync(string path)
        {
            if (hashes.TryGetValue(path, out var cached)) return cached;
            string? hash;
            try { hash = await Sha256Verifier.ComputeAsync(path, cancellationToken); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { hash = null; }
            hashes[path] = hash;
            return hash;
        }

        var results = new List<DependencyImportResult>(_items.Count);
        foreach (var item in _items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = Path.Combine(cacheDirectory, item.ArchiveFileName);
            if (File.Exists(destination) &&
                (item.Size <= 0 || new FileInfo(destination).Length == item.Size) &&
                string.Equals(await HashAsync(destination), item.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(new(item.Id, item.ArchiveFileName, "already-cached", destination));
                continue;
            }

            string? match = null;
            var sameNameButDifferent = false;
            foreach (var file in files)
            {
                if (!Path.GetFileName(file).Equals(item.ArchiveFileName, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(await HashAsync(file), item.Sha256, StringComparison.OrdinalIgnoreCase)) { match = file; break; }
                sameNameButDifferent = true;
            }
            if (match is null && item.Size > 0)
            {
                foreach (var file in files)
                {
                    if (new FileInfo(file).Length != item.Size) continue;
                    if (string.Equals(await HashAsync(file), item.Sha256, StringComparison.OrdinalIgnoreCase)) { match = file; break; }
                }
            }

            if (match is null)
            {
                results.Add(new(item.Id, item.ArchiveFileName,
                    sameNameButDifferent ? "hash-mismatch" : "missing",
                    sameNameButDifferent ? "找到同名文件但 SHA-256 不匹配，已拒绝导入。" : "文件夹中未找到该组件。"));
                continue;
            }

            await CopyVerifiedAsync(match, destination, cancellationToken);
            results.Add(new(item.Id, item.ArchiveFileName, "imported", destination));
        }
        return results;
    }

    private static async Task CopyVerifiedAsync(string source, string destination, CancellationToken cancellationToken)
    {
        var temporary = destination + ".importing";
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
                             128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None,
                             128 * 1024, FileOptions.Asynchronous))
            {
                await input.CopyToAsync(output, cancellationToken);
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
