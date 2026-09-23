namespace RootedAndroidGameVM.Core.Downloads;

/// <summary>
/// A prefix replacement applied to a download URL. The pinned SHA-256 is always verified,
/// so a mirror can never substitute different content.
/// </summary>
public sealed record DownloadSourceRule(string From, string To)
{
    public static DownloadSourceRule Create(string from, string to)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(from);
        ArgumentException.ThrowIfNullOrWhiteSpace(to);
        var normalizedFrom = NormalizePrefix(from, nameof(from));
        var normalizedTo = NormalizePrefix(to, nameof(to));
        return new(normalizedFrom, normalizedTo);
    }

    public bool TryRewrite(Uri source, out Uri rewritten)
    {
        rewritten = source;
        var absolute = source.AbsoluteUri;
        if (!absolute.StartsWith(From, StringComparison.OrdinalIgnoreCase)) return false;
        var candidate = To + absolute[From.Length..];
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) || !IsHttp(uri)) return false;
        rewritten = uri;
        return true;
    }

    internal static bool IsHttp(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;

    internal static string NormalizePrefix(string value, string parameter)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !IsHttp(uri))
            throw new ArgumentException("下载来源必须是 http 或 https 的完整地址。", parameter);
        return uri.AbsoluteUri.TrimEnd('/');
    }
}

public sealed record DownloadSourceConfiguration(int SchemaVersion, IReadOnlyList<DownloadSourceRule> Rules)
{
    public const int CurrentSchemaVersion = 1;

    public static DownloadSourceConfiguration Empty { get; } = new(CurrentSchemaVersion, []);

    public bool HasRules => Rules.Count > 0;

    public Uri Rewrite(Uri source)
    {
        foreach (var rule in Rules)
        {
            if (rule.TryRewrite(source, out var rewritten)) return rewritten;
        }
        return source;
    }

    public static DownloadSourceConfiguration Create(IEnumerable<DownloadSourceRule> rules) =>
        new(CurrentSchemaVersion, rules.ToArray());
}
