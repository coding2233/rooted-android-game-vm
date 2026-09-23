using System.Text.Json;
using RootedAndroidGameVM.Core.Storage;

namespace RootedAndroidGameVM.Core.Downloads;

/// <summary>
/// Persists optional download mirror rules in the control root. Rules only rewrite the
/// download URL; the pinned SHA-256 still verifies every byte.
/// </summary>
public sealed class DownloadSourceConfigurationStore
{
    public const string FileName = "download-mirror.json";

    public string ControlRoot { get; }
    public string FilePath => Path.Combine(ControlRoot, FileName);

    public DownloadSourceConfigurationStore(string? controlRoot = null) =>
        ControlRoot = StoragePathPolicy.NormalizeRoot(controlRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RootedAndroidGameVM"));

    public DownloadSourceConfiguration Read()
    {
        if (!File.Exists(FilePath)) return DownloadSourceConfiguration.Empty;
        try
        {
            StoragePathPolicy.RejectReparsePoints(FilePath);
            var document = JsonSerializer.Deserialize<Document>(File.ReadAllText(FilePath), AtomicJsonFile.Options);
            if (document is null || document.SchemaVersion != DownloadSourceConfiguration.CurrentSchemaVersion)
                throw new InvalidDataException("下载来源配置版本无效。");
            var rules = (document.Rules ?? []).Select(rule => DownloadSourceRule.Create(rule.From, rule.To));
            return DownloadSourceConfiguration.Create(rules);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            throw new InvalidDataException(
                "无法读取下载来源配置，请修复 download-mirror.json 或删除它以恢复直连。", exception);
        }
    }

    public async Task SaveAsync(DownloadSourceConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var document = new Document(
            DownloadSourceConfiguration.CurrentSchemaVersion,
            configuration.Rules.Select(rule => new RuleDocument(rule.From, rule.To)).ToArray());
        await AtomicJsonFile.WriteAsync(FilePath, document, cancellationToken);
    }

    public void Clear()
    {
        if (File.Exists(FilePath)) File.Delete(FilePath);
    }

    private sealed record Document(int SchemaVersion, RuleDocument[]? Rules);
    private sealed record RuleDocument(string From, string To);
}
