namespace RootedAndroidGameVM.Core.Downloads;

public sealed record DownloadMirrorPreset(
    string Id,
    string Name,
    string Description,
    IReadOnlyList<DownloadSourceRule> Rules);

/// <summary>
/// Verified mirror presets. Every preset only rewrites URLs; the pinned SHA-256 still checks
/// every byte, so a mirror cannot substitute content. Presets are a convenience, not trust.
/// </summary>
public static class DownloadMirrorPresets
{
    public static DownloadMirrorPreset China { get; } = new(
        "china",
        "国内镜像（腾讯云 + ghfast）",
        "Google SDK 走腾讯云 AndroidSDK 镜像，GitHub 走 ghfast 代理；Microsoft JDK 无国内镜像，保持直连。仍强制 SHA-256 校验。",
        [
            DownloadSourceRule.Create(
                "https://dl.google.com/android/repository/",
                "https://mirrors.cloud.tencent.com/AndroidSDK/"),
            DownloadSourceRule.Create(
                "https://github.com/",
                "https://ghfast.top/https://github.com/")
        ]);

    public static DownloadMirrorPreset ChinaAlternateGitHub { get; } = new(
        "china-alt",
        "国内镜像（腾讯云 + gh-proxy）",
        "与国内预设相同，但 GitHub 改用 gh-proxy.com，适合 ghfast 不可用时切换。仍强制 SHA-256 校验。",
        [
            DownloadSourceRule.Create(
                "https://dl.google.com/android/repository/",
                "https://mirrors.cloud.tencent.com/AndroidSDK/"),
            DownloadSourceRule.Create(
                "https://github.com/",
                "https://gh-proxy.com/https://github.com/")
        ]);

    public static IReadOnlyList<DownloadMirrorPreset> All { get; } = [China, ChinaAlternateGitHub];

    public static DownloadMirrorPreset? Find(string id) =>
        All.FirstOrDefault(preset => preset.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
}
