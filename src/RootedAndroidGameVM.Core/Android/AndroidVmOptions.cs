using RootedAndroidGameVM.Core.Setup;

namespace RootedAndroidGameVM.Core.Android;

public sealed record AndroidVmOptions(
    string AvdName,
    string Serial,
    int Port,
    string GpuMode,
    int MemoryMb,
    string? AvdHome = null,
    bool Headless = false,
    bool Verbose = false,
    int? GrpcPort = null,
    int CpuCores = 4,
    bool Vulkan = false,
    int StartAvailableMb = 0,
    bool LowRam = false)
{
    public static AndroidVmOptions ForPaths(InstallPaths paths)
    {
        var profile = new RuntimeProfileStore(paths).Read();
        return new(
            "rooted_android_game_vm_api35", "emulator-5554", 5554,
            profile.Renderer, profile.MemoryMb, paths.AvdHome, CpuCores: profile.CpuCores, Vulkan: profile.Vulkan, StartAvailableMb: profile.StartAvailableMb, LowRam: profile.LowRam);
    }

    public static AndroidVmOptions ProductDefault => ForPaths(InstallPaths.CreateDefault());

    public static AndroidVmOptions Default => ProductDefault;

    /// <summary>Directory holding the AVD definition files; falls back to the Android default home.</summary>
    public string AvdRoot => AvdHome ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".android", "avd");

    public string AvdDirectory => Path.Combine(AvdRoot, AvdName + ".avd");

    public string AvdConfigPath => Path.Combine(AvdDirectory, "config.ini");

    /// <summary>
    /// True when the product AVD has been created. This is the authoritative installation proof:
    /// <c>emulator -list-avds</c> intermittently prints nothing with a zero exit code, so it must
    /// never be the only source for "the virtual machine is installed".
    /// </summary>
    public bool HasAvdConfiguration => File.Exists(AvdConfigPath);
}
