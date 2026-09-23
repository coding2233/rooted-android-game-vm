using RootedAndroidGameVM.Core.Setup;

namespace RootedAndroidGameVM.Core.Android;

public sealed record AndroidSdkLayout(string Root, string AdbPath, string EmulatorPath)
{
    public static AndroidSdkLayout FromRoot(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var normalizedRoot = Path.GetFullPath(root);
        return new(
            normalizedRoot,
            Path.Combine(normalizedRoot, "platform-tools", "adb.exe"),
            Path.Combine(normalizedRoot, "emulator", "emulator.exe"));
    }

    public static AndroidSdkLayout Discover(InstallPaths? paths = null) =>
        FromRoot((paths ?? InstallPaths.CreateDefault()).SdkRoot);

    public bool HasRequiredTools => File.Exists(AdbPath) && File.Exists(EmulatorPath);

    /// <summary>
    /// Locates the command-line tools bin directory. Prefers <c>cmdline-tools\latest</c> and
    /// otherwise falls back to any versioned <c>cmdline-tools\&lt;version&gt;\bin</c>, which is
    /// how a user-managed Android Studio SDK usually lays them out.
    /// </summary>
    public string? FindCommandLineToolsBin()
    {
        var root = Path.Combine(Root, "cmdline-tools");
        if (!Directory.Exists(root)) return null;
        var latest = Path.Combine(root, "latest", "bin");
        if (File.Exists(Path.Combine(latest, "avdmanager.bat"))) return latest;
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var bin = Path.Combine(directory, "bin");
            if (File.Exists(Path.Combine(bin, "avdmanager.bat"))) return bin;
        }
        return null;
    }
}
