using System.Text.Json;
using RootedAndroidGameVM.Core.Storage;

namespace RootedAndroidGameVM.Core.Setup;

/// <summary>
/// Persists the optional SDK/AVD/download-cache overrides in the control root.
/// The file is small control state; it never holds simulator data itself.
/// </summary>
public sealed class InstallPathConfigurationStore
{
    public const string FileName = "install-paths.json";

    public string ControlRoot { get; }
    public string FilePath => Path.Combine(ControlRoot, FileName);

    public InstallPathConfigurationStore(string? controlRoot = null) =>
        ControlRoot = StoragePathPolicy.NormalizeRoot(controlRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RootedAndroidGameVM"));

    public InstallPathConfiguration Read()
    {
        if (!File.Exists(FilePath)) return InstallPathConfiguration.Empty;
        try
        {
            StoragePathPolicy.RejectReparsePoints(FilePath);
            var document = JsonSerializer.Deserialize<Document>(File.ReadAllText(FilePath), AtomicJsonFile.Options);
            if (document is null || document.SchemaVersion != InstallPathConfiguration.CurrentSchemaVersion)
                throw new InvalidDataException("安装路径配置版本无效。");
            return InstallPathConfiguration.Create(document.SdkRoot, document.AvdHome, document.DownloadCache);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            throw new InvalidDataException(
                "无法读取安装路径配置，请修复 install-paths.json 或删除它以恢复默认路径。", exception);
        }
    }

    public async Task SaveAsync(InstallPathConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        await AtomicJsonFile.WriteAsync(
            FilePath,
            new Document(InstallPathConfiguration.CurrentSchemaVersion,
                configuration.SdkRoot, configuration.AvdHome, configuration.DownloadCache),
            cancellationToken);
    }

    public void Clear()
    {
        if (File.Exists(FilePath)) File.Delete(FilePath);
    }

    private sealed record Document(int SchemaVersion, string? SdkRoot, string? AvdHome, string? DownloadCache);
}
