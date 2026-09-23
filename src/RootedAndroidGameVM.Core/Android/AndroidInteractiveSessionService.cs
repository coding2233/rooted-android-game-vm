using RootedAndroidGameVM.Core.Processes;

namespace RootedAndroidGameVM.Core.Android;

/// <summary>
/// Wakes the guest and dismisses the keyguard after boot. The input commands can fail
/// transiently while the input manager is still coming up, so this retries and confirms the
/// real device state instead of failing on a single non-zero exit code.
/// </summary>
public sealed class AndroidInteractiveSessionService(
    AndroidSdkLayout layout,
    AndroidVmOptions options,
    IProcessRunner runner)
{
    private const int MaxAttempts = 3;

    public async Task PrepareAsync(CancellationToken cancellationToken = default)
    {
        string? lastFailure = null;
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var wake = await runner.RunAsync(
                AndroidCommandFactory.WakeDevice(layout, options),
                cancellationToken);
            var dismiss = await runner.RunAsync(
                AndroidCommandFactory.DismissKeyguard(layout, options),
                cancellationToken);
            if (wake.ExitCode == 0 && dismiss.ExitCode == 0) return;

            lastFailure = $"唤醒安卓虚拟机失败：{Detail(wake)}{Detail(dismiss)}";
            // A transient input failure does not mean the device is asleep or locked.
            if (await IsAwakeAndUnlockedAsync(cancellationToken)) return;
            if (attempt < MaxAttempts)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }

        throw new InvalidOperationException(lastFailure ?? "唤醒安卓虚拟机失败。");
    }

    private async Task<bool> IsAwakeAndUnlockedAsync(CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(
            AndroidCommandFactory.Adb(
                layout,
                options,
                "shell",
                "dumpsys power | grep mWakefulness; dumpsys window policy"),
            cancellationToken);
        if (result.ExitCode != 0) return false;
        var text = result.StandardOutput;
        var awake = text.Contains("mWakefulness=Awake", StringComparison.Ordinal);
        var locked = text.Contains("mShowingLockscreen=true", StringComparison.Ordinal)
            || text.Contains("showing=true", StringComparison.Ordinal)
            || text.Contains("isStatusBarKeyguard=true", StringComparison.Ordinal);
        return awake && !locked;
    }

    private static string Detail(ProcessResult result)
    {
        var text = string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput
            : result.StandardError;
        return text.Trim();
    }
}
