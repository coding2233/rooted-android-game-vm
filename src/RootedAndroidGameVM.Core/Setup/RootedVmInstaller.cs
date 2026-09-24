using System.IO.Compression;
using System.Text.Json;
using RootedAndroidGameVM.Core.Android;
using RootedAndroidGameVM.Core.Dependencies;
using RootedAndroidGameVM.Core.Downloads;
using RootedAndroidGameVM.Core.Processes;
using RootedAndroidGameVM.Core.Ui;

namespace RootedAndroidGameVM.Core.Setup;

public sealed class RootedVmInstaller
{
    private readonly InstallPaths _paths;
    private readonly ProcessRunner _runner;
    private readonly VerifiedDownloader _downloader;
    private AndroidVmOptions _options;

    public RootedVmInstaller(
        InstallPaths? paths = null,
        ProcessRunner? runner = null,
        HttpClient? httpClient = null,
        AndroidVmOptions? options = null)
    {
        _paths = paths ?? InstallPaths.CreateDefault();
        _runner = runner ?? new ProcessRunner();
        _options = options ?? AndroidVmOptions.ForPaths(_paths);
        var downloadSource = new DownloadSourceConfigurationStore().Read();
        _downloader = new VerifiedDownloader(
            httpClient ?? new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan },
            retryDelay: null,
            sourceRewriter: downloadSource.Rewrite);
    }

    /// <summary>
    /// Installs or upgrades the product environment. Returns true when Root was verified; Root
    /// is an optional capability, so a Root-only failure downgrades to a warning instead of
    /// blocking an otherwise working virtual machine.
    /// </summary>
    public async Task<bool> InstallAsync(
        bool sdkLicenseAccepted,
        IProgress<SetupProgressState>? progress = null,
        CancellationToken cancellationToken = default,
        bool adoptExistingEnvironment = false)
    {
        if (File.Exists(Path.Combine(_paths.ProductRoot, "debug-restore.json")))
            throw new InvalidOperationException("存在未完成的检查点恢复。请先在调试工作台恢复中断操作；不会重新初始化虚拟机。");
        if (!sdkLicenseAccepted)
        {
            throw new InvalidOperationException("必须先阅读并接受 Android SDK 许可协议。");
        }

        _downloader.Progress = SetupDownloadProgress.Create(progress);

        Report(progress, SetupStage.Preflight);
        RunPreflight();
        Directory.CreateDirectory(_paths.ProductRoot);
        Directory.CreateDirectory(_paths.RuntimeRoot);
        Directory.CreateDirectory(_paths.DownloadCache);
        await DependencyDownloadCatalog.AdoptAllAsync(_paths.DownloadCache, cancellationToken: cancellationToken);
        await WriteJournalAsync(SetupStage.Preflight, cancellationToken);

        if (_paths.SdkIsExternal)
        {
            return await VerifyExternalSdkAsync(progress, cancellationToken);
        }

        if (adoptExistingEnvironment)
        {
            var existingLayout = AndroidSdkLayout.Discover();
            if (existingLayout.HasRequiredTools)
            {
                var existingController = new AndroidVmController(existingLayout, _options);
                if (await existingController.GetStatusAsync(cancellationToken) != VmStatus.NotInstalled)
                {
                    return await VerifyAndRecordAsync(existingLayout, existingController, progress, cancellationToken, rootProblem: null);
                }
            }
        }

        _options = _options with { AvdHome = _options.AvdHome ?? _paths.AvdHome };
        Directory.CreateDirectory(_options.AvdHome);

        Report(progress, SetupStage.Download);
        await WriteJournalAsync(SetupStage.Download, cancellationToken);
        await PrepareJavaAsync(cancellationToken);
        var layout = AndroidSdkLayout.FromRoot(_paths.SdkRoot);
        await PrepareCommandLineToolsAsync(layout, cancellationToken);
        await InstallSdkPackagesAsync(layout, cancellationToken);
        SdkComponentRevisionVerifier.Verify(layout);
        await VerifyAccelerationAsync(layout, cancellationToken);

        Report(progress, SetupStage.CreateAvd);
        await WriteJournalAsync(SetupStage.CreateAvd, cancellationToken);
        await CreateAvdAsync(layout, cancellationToken);
        var controller = new AndroidVmController(layout, _options);
        await controller.StartAsync(cancellationToken);

        Report(progress, SetupStage.Root);
        await WriteJournalAsync(SetupStage.Root, cancellationToken);
        string? rootProblem = null;
        if (await GetRootPreparationStateAsync(layout, cancellationToken) == RootPreparationState.NeedsPatch)
        {
            try
            {
                await PrepareRootToolsAsync(cancellationToken);
                await RestoreStockRamdiskWhenAvailableAsync(layout, cancellationToken);
                await PatchRootAsync(layout, cancellationToken);
                await RecordRamdiskHashesAsync(layout, cancellationToken);
                await controller.StopAsync(cancellationToken);
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
                DeleteAvdInitrdCache();
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // Root is optional: record the failure and keep installing the virtual machine.
                rootProblem = error.Message;
            }
        }

        return await VerifyAndRecordAsync(layout, controller, progress, cancellationToken, rootProblem);
    }

    /// <summary>
    /// Path for a user-provided external SDK. The product never touches the external system
    /// images or platform-tools, but it will install the pinned command-line tools into the
    /// SDK when they are missing so avdmanager can create the product AVD. It requires an
    /// already-rooted system image and fails closed otherwise.
    /// </summary>
    private async Task<bool> VerifyExternalSdkAsync(
        IProgress<SetupProgressState>? progress,
        CancellationToken cancellationToken)
    {
        Report(progress, SetupStage.Download);
        await WriteJournalAsync(SetupStage.Download, cancellationToken);
        var layout = AndroidSdkLayout.FromRoot(_paths.SdkRoot);
        if (!layout.HasRequiredTools)
        {
            throw new FileNotFoundException(
                $"外部 SDK 缺少 platform-tools\\adb.exe 或 emulator\\emulator.exe：{layout.Root}。");
        }
        await PrepareJavaAsync(cancellationToken);
        await EnsureCommandLineToolsAsync(layout, cancellationToken);
        VerifyExternalSdkComponents(layout);
        await VerifyAccelerationAsync(layout, cancellationToken);

        _options = _options with { AvdHome = _options.AvdHome ?? _paths.AvdHome };
        Directory.CreateDirectory(_options.AvdHome);

        Report(progress, SetupStage.CreateAvd);
        await WriteJournalAsync(SetupStage.CreateAvd, cancellationToken);
        await CreateAvdAsync(layout, cancellationToken);
        var controller = new AndroidVmController(layout, _options);
        await controller.StartAsync(cancellationToken);

        Report(progress, SetupStage.Root);
        await WriteJournalAsync(SetupStage.Root, cancellationToken);
        if (await GetRootPreparationStateAsync(layout, cancellationToken) == RootPreparationState.NeedsPatch)
        {
            await controller.StopAsync(cancellationToken);
            throw new InvalidOperationException(
                "外部 SDK 的系统镜像尚未 Root。产品不会改写外部 SDK；请改用产品自管 SDK，或先在外部环境完成 Root。");
        }

        return await VerifyAndRecordAsync(layout, controller, progress, cancellationToken, rootProblem: null);
    }

    private async Task EnsureCommandLineToolsAsync(AndroidSdkLayout layout, CancellationToken cancellationToken)
    {
        if (layout.FindCommandLineToolsBin() is not null) return;
        await PrepareCommandLineToolsAsync(layout, cancellationToken);
    }

    private static void VerifyExternalSdkComponents(AndroidSdkLayout layout)
    {
        if (!layout.HasRequiredTools)
        {
            throw new FileNotFoundException(
                $"外部 SDK 缺少 platform-tools\\adb.exe 或 emulator\\emulator.exe：{layout.Root}。");
        }
        if (layout.FindCommandLineToolsBin() is null)
        {
            throw new FileNotFoundException(
                $"外部 SDK 缺少 cmdline-tools（需要 avdmanager.bat）：{layout.Root}。");
        }
        foreach (var component in InstallProfile.SdkComponents)
        {
            var directory = Path.Combine(layout.Root, component.RelativeDirectory);
            if (!Directory.Exists(directory))
            {
                throw new DirectoryNotFoundException(
                    $"外部 SDK 缺少所需组件 '{component.PackagePath}'（{directory}）。");
            }
        }
        var ramdisk = Path.Combine(
            layout.Root,
            "system-images", "android-35", "google_apis_playstore", "x86_64", "ramdisk.img");
        if (!File.Exists(ramdisk))
        {
            throw new FileNotFoundException("外部 SDK 的系统镜像缺少 ramdisk.img。", ramdisk);
        }
    }

    private async Task<RootPreparationState> GetRootPreparationStateAsync(
        AndroidSdkLayout layout,
        CancellationToken cancellationToken)
    {
        var ramdisk = Path.Combine(
            layout.Root,
            "system-images",
            "android-35",
            "google_apis_playstore",
            "x86_64",
            "ramdisk.img");
        var backup = ramdisk + ".backup";
        var ramdiskMatchesStock = false;
        var currentHash = File.Exists(ramdisk)
            ? await Security.Sha256Verifier.ComputeAsync(ramdisk, cancellationToken)
            : string.Empty;
        var journal = await Journal.LoadAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(journal?.PatchedRamdiskSha256))
        {
            if (string.Equals(currentHash, journal.StockRamdiskSha256, StringComparison.OrdinalIgnoreCase))
            {
                ramdiskMatchesStock = true;
            }
            else if (!string.Equals(currentHash, journal.PatchedRamdiskSha256, StringComparison.OrdinalIgnoreCase))
            {
                return RootPreparationState.NeedsPatch;
            }
        }
        if (File.Exists(ramdisk) && File.Exists(backup))
        {
            var backupHash = await Security.Sha256Verifier.ComputeAsync(backup, cancellationToken);
            ramdiskMatchesStock = ramdiskMatchesStock ||
                string.Equals(currentHash, backupHash, StringComparison.OrdinalIgnoreCase);
        }

        var identity = await _runner.RunAsync(
            AndroidCommandFactory.RootIdentity(layout, _options),
            cancellationToken);
        var su = await _runner.RunAsync(
            AndroidCommandFactory.FindRootShell(layout, _options),
            cancellationToken);
        return RootPreparationClassifier.Classify(ramdiskMatchesStock, identity, su);
    }

    private async Task RestoreStockRamdiskWhenAvailableAsync(
        AndroidSdkLayout layout,
        CancellationToken cancellationToken)
    {
        var ramdisk = Path.Combine(
            layout.Root,
            "system-images",
            "android-35",
            "google_apis_playstore",
            "x86_64",
            "ramdisk.img");
        var backup = ramdisk + ".backup";
        if (File.Exists(backup))
        {
            var journal = await Journal.LoadAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(journal?.StockRamdiskSha256))
            {
                var backupHash = await Security.Sha256Verifier.ComputeAsync(backup, cancellationToken);
                if (!string.Equals(
                        backupHash,
                        journal.StockRamdiskSha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        "ramdisk.img.backup does not match the install journal stock hash.");
                }
            }
            File.Copy(backup, ramdisk, overwrite: true);
        }
    }

    private async Task RecordRamdiskHashesAsync(
        AndroidSdkLayout layout,
        CancellationToken cancellationToken)
    {
        var ramdisk = Path.Combine(
            layout.Root,
            "system-images",
            "android-35",
            "google_apis_playstore",
            "x86_64",
            "ramdisk.img");
        var backup = ramdisk + ".backup";
        if (!File.Exists(ramdisk) || !File.Exists(backup))
        {
            throw new FileNotFoundException(
                "RootAVD did not preserve both stock and patched ramdisk files.");
        }
        await Journal.UpdateAsync(
            SetupStage.Root,
            layout.Root,
            _options.AvdHome ?? string.Empty,
            _options.AvdName,
            await Security.Sha256Verifier.ComputeAsync(backup, cancellationToken),
            await Security.Sha256Verifier.ComputeAsync(ramdisk, cancellationToken),
            cancellationToken);
    }

    private void DeleteAvdInitrdCache()
    {
        var initrd = Path.Combine(_options.AvdDirectory, "initrd");
        if (File.Exists(initrd))
        {
            File.SetAttributes(initrd, FileAttributes.Normal);
            File.Delete(initrd);
        }
    }

    private void RunPreflight()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) ||
            !Environment.Is64BitOperatingSystem)
        {
            throw new PlatformNotSupportedException("需要 64 位 Windows 11。");
        }

        var root = Path.GetPathRoot(_paths.ProductRoot)
            ?? throw new InvalidOperationException("无法确定安装磁盘。");
        var drive = new DriveInfo(root);
        if (drive.AvailableFreeSpace < InstallProfile.MinimumFreeBytes)
        {
            throw new IOException($"安装磁盘至少需要 24 GB 可用空间，当前约 {drive.AvailableFreeSpace / 1024 / 1024 / 1024} GB。");
        }
    }

    private async Task PrepareJavaAsync(CancellationToken cancellationToken)
    {
        var component = DependencyManifest.LoadEmbedded().Required("microsoft-openjdk");
        await new ProductArchiveInstaller(_downloader).InstallAsync(
            component,
            _paths.DownloadCache,
            _paths.ProductRoot,
            archiveTopLevelDirectory: null,
            targetRelativeDirectory: Path.GetRelativePath(_paths.ProductRoot, _paths.JavaHome),
            revisionFileRelativePath: "release",
            revisionProperty: "JAVA_VERSION",
            expectedRevision: component.Version,
            cancellationToken);
    }

    private async Task PrepareCommandLineToolsAsync(
        AndroidSdkLayout layout,
        CancellationToken cancellationToken)
    {
        var component = DependencyManifest.LoadEmbedded().Required("android-command-line-tools");
        await new ProductArchiveInstaller(_downloader).InstallAsync(
            component,
            _paths.DownloadCache,
            layout.Root,
            "cmdline-tools",
            Path.Combine("cmdline-tools", "latest"),
            "source.properties",
            "Pkg.Revision",
            component.Version,
            cancellationToken);
    }

    private async Task InstallSdkPackagesAsync(
        AndroidSdkLayout layout,
        CancellationToken cancellationToken)
    {
        var environment = CreateSdkEnvironment(layout);
        var sdkManager = GetSdkManagerPath(layout);
        var acceptLicenses = new ProcessRequest(
            new ProcessSpec(sdkManager, [$"--sdk_root={layout.Root}", "--licenses"], layout.Root),
            string.Concat(Enumerable.Repeat("y\n", 32)),
            environment);
        EnsureSuccess(await _runner.RunRequestAsync(acceptLicenses, cancellationToken), "接受 Android SDK 许可");

        var manifest = DependencyManifest.LoadEmbedded();
        var archiveInstaller = new SdkArchiveInstaller(_downloader);
        await InstallSdkArchiveIfMissingAsync(
            layout,
            archiveInstaller,
            manifest.Required("android-platform-tools"),
            InstallProfile.SdkComponents.Single(component => component.PackagePath == "platform-tools"),
            "platform-tools",
            cancellationToken);
        await InstallSdkArchiveIfMissingAsync(
            layout,
            archiveInstaller,
            manifest.Required("android-emulator"),
            InstallProfile.SdkComponents.Single(component => component.PackagePath == "emulator"),
            "emulator",
            cancellationToken);
        await InstallSdkArchiveIfMissingAsync(
            layout,
            archiveInstaller,
            manifest.Required("android-system-image-api35-playstore-x86_64"),
            InstallProfile.SdkComponents.Single(component => component.PackagePath == InstallProfile.SystemImagePackage),
            "x86_64",
            cancellationToken);
    }

    private async Task InstallSdkArchiveIfMissingAsync(
        AndroidSdkLayout layout,
        SdkArchiveInstaller archiveInstaller,
        DependencyComponent dependency,
        PinnedSdkComponent component,
        string archiveTopLevelDirectory,
        CancellationToken cancellationToken)
    {
        if (SdkComponentRevisionVerifier.IsInstalled(layout, component))
        {
            await archiveInstaller.EnsureGenericRegistrationAsync(
                dependency,
                layout.Root,
                component.RelativeDirectory,
                cancellationToken);
            return;
        }
        await archiveInstaller.InstallAsync(
            dependency,
            _paths.DownloadCache,
            layout.Root,
            archiveTopLevelDirectory,
            component.RelativeDirectory,
            cancellationToken);
    }

    private async Task VerifyAccelerationAsync(
        AndroidSdkLayout layout,
        CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(
            new ProcessSpec(
                layout.EmulatorPath,
                ["-accel-check"],
                Path.GetDirectoryName(layout.EmulatorPath)),
            cancellationToken);
        EnsureSuccess(result, "检查 Windows Hypervisor Platform");
    }

    private async Task CreateAvdAsync(AndroidSdkLayout layout, CancellationToken cancellationToken)
    {
        var options = _options;
        // The AVD definition file decides whether the AVD must be created. The emulator's
        // "-list-avds" probe intermittently prints nothing with a zero exit code, and acting on
        // that empty output would re-run "avdmanager create avd --force" over an existing AVD.
        var exists = options.HasAvdConfiguration;
        if (!exists)
        {
            var avdManagerBin = layout.FindCommandLineToolsBin()
                ?? throw new FileNotFoundException("找不到 Android SDK 命令行工具（cmdline-tools）。", layout.Root);
            var avdManager = Path.Combine(avdManagerBin, "avdmanager.bat");
            var request = new ProcessRequest(
                new ProcessSpec(
                    avdManager,
                    ["create", "avd", "--force", "--name", options.AvdName, "--package",
                        InstallProfile.SystemImagePackage, "--device", "pixel_7"],
                    layout.Root),
                "no\n",
                CreateSdkEnvironment(layout));
            EnsureSuccess(await _runner.RunRequestAsync(request, cancellationToken), "创建 Android 虚拟机");
        }

        var config = options.AvdConfigPath;
        if (!File.Exists(config))
        {
            throw new FileNotFoundException("AVD 已创建但找不到配置文件。", config);
        }

        var runtimeProfile = RuntimeProfile.Recommended with
        {
            Renderer = _options.GpuMode,
            MemoryMb = _options.MemoryMb,
            CpuCores = _options.CpuCores,
            Vulkan = _options.Vulkan
        };
        var settings = new Dictionary<string, string>(runtimeProfile.ToAvdSettings())
        {
            ["disk.dataPartition.size"] = "12G",
            ["hw.keyboard"] = "yes",
            ["vm.heapSize"] = "512"
        };
        await AvdConfigEditor.UpsertAsync(config, settings, cancellationToken);
    }

    private async Task PrepareRootToolsAsync(CancellationToken cancellationToken)
    {
        var batch = Path.Combine(_paths.RootAvdRoot, "rootAVD.bat");
        if (!File.Exists(batch))
        {
            var archive = await DownloadAsync(InstallProfile.RootAvd, cancellationToken);
            var staging = CreateStagingDirectory("rootavd");
            ZipFile.ExtractToDirectory(archive, staging);
            var source = Directory.GetDirectories(staging).Single();
            Directory.CreateDirectory(Path.GetDirectoryName(_paths.RootAvdRoot)!);
            Directory.Move(source, _paths.RootAvdRoot);
        }

        foreach (var bundled in Directory.EnumerateFiles(_paths.RootAvdRoot, "Magisk*.zip", SearchOption.TopDirectoryOnly))
        {
            File.Delete(bundled);
        }

        var appsDirectory = Path.Combine(_paths.RootAvdRoot, "Apps");
        if (File.Exists(appsDirectory))
        {
            File.Delete(appsDirectory);
        }
        Directory.CreateDirectory(appsDirectory);
        foreach (var oldApk in Directory.EnumerateFiles(appsDirectory, "*.apk", SearchOption.TopDirectoryOnly))
        {
            File.Delete(oldApk);
        }

        var magisk = await DownloadAsync(InstallProfile.Magisk, cancellationToken);
        File.Copy(magisk, Path.Combine(_paths.RootAvdRoot, "Magisk30.zip"), overwrite: true);
        File.Copy(magisk, Path.Combine(_paths.RootAvdRoot, "Magisk.zip"), overwrite: true);
    }

    private async Task PatchRootAsync(AndroidSdkLayout layout, CancellationToken cancellationToken)
    {
        var batch = Path.Combine(_paths.RootAvdRoot, "rootAVD.bat");
        var relativeRamdisk = @"system-images\android-35\google_apis_playstore\x86_64\ramdisk.img";
        if (!File.Exists(batch))
        {
            throw new FileNotFoundException("找不到 RootAVD 批处理入口。", batch);
        }

        await _runner.RunAsync(
            AndroidCommandFactory.Adb(
                layout,
                _options,
                "shell",
                "rm",
                "-f",
                "/sdcard/Download/fakeboot.img",
                "/sdcard/Download/magisk_patched*.img"),
            cancellationToken);

        var patchedCheck = AndroidCommandFactory.Adb(
            layout,
            _options,
            "shell",
            "ls",
            "/sdcard/Download/magisk_patched*.img");
        var patched = await _runner.RunAsync(patchedCheck, cancellationToken);
        if (patched.ExitCode != 0)
        {
            await RunRootAvdAsync(layout, relativeRamdisk, cancellationToken);
            var fakeBoot = await _runner.RunAsync(
                AndroidCommandFactory.Adb(
                    layout,
                    _options,
                    "shell",
                    "test",
                    "-f",
                    "/sdcard/Download/fakeboot.img"),
                cancellationToken);
            EnsureSuccess(fakeBoot, "生成 Magisk FAKEBOOTIMG");

            await new MagiskCliPatchService(layout, _options, _runner)
                .PatchAsync(cancellationToken);
        }

        var finalResult = await RunRootAvdAsync(layout, relativeRamdisk, cancellationToken);
        EnsureSuccess(finalResult, "配置 Root");
    }

    private async Task<ProcessResult> RunRootAvdAsync(
        AndroidSdkLayout layout,
        string relativeRamdisk,
        CancellationToken cancellationToken)
    {
        var command = $"rootAVD.bat {relativeRamdisk} FAKEBOOTIMG";
        var request = new ProcessRequest(
            new ProcessSpec(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
                ["/d", "/s", "/c", command],
                _paths.RootAvdRoot),
            EnvironmentVariables: CreateSdkEnvironment(layout));
        return await _runner.RunRequestAsync(request, cancellationToken);
    }

    private async Task<bool> VerifyAndRecordAsync(
        AndroidSdkLayout layout,
        AndroidVmController controller,
        IProgress<SetupProgressState>? progress,
        CancellationToken cancellationToken,
        string? rootProblem)
    {
        Report(progress, SetupStage.Verify);
        await WriteJournalAsync(SetupStage.Verify, cancellationToken);
        await EnsureRunningAsync(controller, cancellationToken);

        var rootVerified = rootProblem is null;
        if (rootVerified)
        {
            try
            {
                await EnsureMagiskAppInstalledAsync(layout, cancellationToken);
                var policyAutomator = new MagiskPolicyAutomator(layout, _options, _runner);
                var diagnostics = await DiagnoseRunningAsync(controller, cancellationToken);
                if (!IsRootVerified(diagnostics))
                {
                    await policyAutomator.GrantShellAsync(cancellationToken);
                    // The grant flow can reboot the guest through Magisk's additional setup;
                    // reconfirm the connection before the final Root diagnosis.
                    diagnostics = await DiagnoseRunningAsync(controller, cancellationToken);
                    if (!IsRootVerified(diagnostics))
                    {
                        throw new InvalidOperationException($"Root 最终验证失败。\n{diagnostics}");
                    }
                }
                else
                {
                    await policyAutomator.PersistCurrentShellPolicyAsync(cancellationToken);
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // Root is optional: keep the install result and surface the reason.
                rootVerified = false;
                rootProblem = error.Message;
            }
        }

        await VerifyPersistentRootAndHealthAsync(layout, controller, cancellationToken, rootVerified);

        Directory.CreateDirectory(_paths.ProductRoot);
        var marker = new
        {
            version = InstallProfile.ProductVersion,
            sdkRoot = layout.Root,
            avdName = _options.AvdName,
            avdHome = _options.AvdHome,
            rootVerified,
            rootProblem,
            verifiedAtUtc = DateTimeOffset.UtcNow
        };
        await File.WriteAllTextAsync(
            Path.Combine(_paths.ProductRoot, "install.json"),
            JsonSerializer.Serialize(marker, new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken);
        Report(progress, SetupStage.Complete);
        await WriteJournalAsync(SetupStage.Complete, cancellationToken);
        return rootVerified;
    }

    private async Task EnsureMagiskAppInstalledAsync(
        AndroidSdkLayout layout,
        CancellationToken cancellationToken)
    {
        var package = await _runner.RunAsync(
            AndroidCommandFactory.Adb(
                layout,
                _options,
                "shell",
                "pm",
                "path",
                "com.topjohnwu.magisk"),
            cancellationToken);
        if (package.ExitCode == 0 && package.StandardOutput.Contains("package:", StringComparison.Ordinal))
        {
            return;
        }

        var apk = await DownloadAsync(InstallProfile.Magisk, cancellationToken);
        EnsureSuccess(await _runner.RunAsync(
            AndroidCommandFactory.InstallApk(layout, _options, apk),
            cancellationToken), "安装 Magisk 管理应用");
    }

    private async Task WriteJournalAsync(
        SetupStage stage,
        CancellationToken cancellationToken)
    {
        await Journal.UpdateAsync(
            stage,
            _paths.SdkRoot,
            _options.AvdHome ?? string.Empty,
            _options.AvdName,
            cancellationToken: cancellationToken);
    }

    private async Task VerifyPersistentRootAndHealthAsync(
        AndroidSdkLayout layout,
        AndroidVmController controller,
        CancellationToken cancellationToken,
        bool rootVerified)
    {
        var bootIdBefore = await ReadBootIdAsync(layout, cancellationToken);
        var uptimeBefore = await ReadUptimeSecondsAsync(layout, cancellationToken);
        await controller.StopAsync(cancellationToken);
        await EnsureEmulatorOfflineAsync(layout, cancellationToken);
        await controller.StartAsync(cancellationToken);
        var bootIdAfter = await ReadBootIdAsync(layout, cancellationToken);
        var uptimeAfter = await ReadUptimeSecondsAsync(layout, cancellationToken);
        if (!DidGuestReboot(bootIdBefore, uptimeBefore, bootIdAfter, uptimeAfter))
        {
            // Stop/Start status probes can transiently misread and skip the actual restart;
            // verify deterministically and retry the whole cold cycle once.
            await controller.StopAsync(cancellationToken);
            await EnsureEmulatorOfflineAsync(layout, cancellationToken);
            await controller.StartAsync(cancellationToken);
            bootIdAfter = await ReadBootIdAsync(layout, cancellationToken);
            uptimeAfter = await ReadUptimeSecondsAsync(layout, cancellationToken);
        }
        if (!DidGuestReboot(bootIdBefore, uptimeBefore, bootIdAfter, uptimeAfter))
        {
            throw new InvalidOperationException(
                "冷重启验证失败：Android 系统未发生真正的重启（boot ID 与运行时长均未变化）。");
        }

        if (!rootVerified)
        {
            // Root is optional: keep the restart and graphics evidence, skip Root-owned probes.
            await VerifyScreenshotAsync(layout, cancellationToken);
            return;
        }

        var diagnostics = await DiagnoseRunningAsync(controller, cancellationToken);
        if (!IsRootVerified(diagnostics))
        {
            throw new InvalidOperationException($"冷重启后 Root 未保持。{Environment.NewLine}{diagnostics}");
        }

        var version = await _runner.RunAsync(
            AndroidCommandFactory.RootShell(
                layout,
                _options,
                "magisk -v"),
            cancellationToken);
        EnsureSuccess(version, "验证 Magisk 版本");
        if (!version.StandardOutput.Contains("30.6", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Magisk 版本不匹配，预期 30.6，实际为 {version.StandardOutput.Trim()}。");
        }

        const string healthFile = "/data/adb/rgvm-health";
        EnsureSuccess(await _runner.RunAsync(
            AndroidCommandFactory.RootShell(
                layout,
                _options,
                $"touch {healthFile}"),
            cancellationToken), "验证 Root 数据写入");
        var readBack = await _runner.RunAsync(
            AndroidCommandFactory.RootShell(
                layout,
                _options,
                $"test -f {healthFile}"),
            cancellationToken);
        EnsureSuccess(readBack, "验证 Root 数据读取");
        await _runner.RunAsync(
            AndroidCommandFactory.RootShell(
                layout,
                _options,
                $"rm -f {healthFile}"),
            CancellationToken.None);

        await VerifyScreenshotAsync(layout, cancellationToken);
    }

    private async Task VerifyScreenshotAsync(
        AndroidSdkLayout layout,
        CancellationToken cancellationToken)
    {
        const string screenshot = "/data/local/tmp/rgvm-health.png";
        EnsureSuccess(await _runner.RunAsync(
            AndroidCommandFactory.Adb(
                layout,
                _options,
                "shell",
                "screencap",
                "-p",
                screenshot),
            cancellationToken), "验证图形截图");
        EnsureSuccess(await _runner.RunAsync(
            AndroidCommandFactory.Adb(
                layout,
                _options,
                "shell",
                "test",
                "-s",
                screenshot),
            cancellationToken), "验证图形截图内容");
        await _runner.RunAsync(
            AndroidCommandFactory.Adb(layout, _options, "shell", "rm", "-f", screenshot),
            CancellationToken.None);
    }

    private async Task<string> ReadBootIdAsync(
        AndroidSdkLayout layout,
        CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(
            AndroidCommandFactory.Adb(
                layout,
                _options,
                "shell",
                "cat",
                "/proc/sys/kernel/random/boot_id"),
            cancellationToken);
        EnsureSuccess(result, "读取 Android boot ID");
        return result.StandardOutput.Trim();
    }

    private async Task<double> ReadUptimeSecondsAsync(
        AndroidSdkLayout layout,
        CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(
            AndroidCommandFactory.Adb(
                layout,
                _options,
                "shell",
                "cat",
                "/proc/uptime"),
            cancellationToken);
        EnsureSuccess(result, "读取 Android 运行时长");
        var first = result.StandardOutput.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        return double.TryParse(first, System.Globalization.CultureInfo.InvariantCulture, out var uptime)
            ? uptime
            : -1;
    }

    /// <summary>
    /// A real guest reboot shows a new boot_id, or — should a kernel/quirk reuse it — a clearly
    /// younger uptime. Both signals together make the cold-restart evidence robust.
    /// </summary>
    private static bool DidGuestReboot(
        string bootIdBefore,
        double uptimeBefore,
        string bootIdAfter,
        double uptimeAfter) =>
        !string.Equals(bootIdBefore, bootIdAfter, StringComparison.Ordinal) ||
        (uptimeBefore > 0 && uptimeAfter > 0 && uptimeAfter < uptimeBefore);

    private static bool IsRootVerified(string diagnostics) =>
        diagnostics.Contains("Root：正常（uid=0）", StringComparison.Ordinal);

    /// <summary>
    /// A single "not running" answer can be a transient ADB misread, and acting on it would start
    /// a second emulator over a live guest. Confirm twice before starting, then let the diagnosis
    /// retry a few times so a guest that is still rebooting is not judged as failed.
    /// </summary>
    private static async Task EnsureRunningAsync(
        AndroidVmController controller,
        CancellationToken cancellationToken)
    {
        if (await controller.GetStatusAsync(cancellationToken) == VmStatus.Running) return;
        await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
        if (await controller.GetStatusAsync(cancellationToken) == VmStatus.Running) return;
        await controller.StartAsync(cancellationToken);
    }

    private static async Task<string> DiagnoseRunningAsync(
        AndroidVmController controller,
        CancellationToken cancellationToken)
    {
        string? diagnostics = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            await EnsureRunningAsync(controller, cancellationToken);
            diagnostics = await controller.DiagnoseAsync(cancellationToken);
            if (diagnostics.Contains("虚拟机：运行中", StringComparison.Ordinal)) return diagnostics;
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        }

        return diagnostics ?? string.Empty;
    }

    /// <summary>
    /// Stop/Start status probes are based on transient ADB answers, so "already stopped" can be
    /// wrong while the emulator is still alive. The cold-restart evidence requires a real stop:
    /// keep issuing "emu kill" until the product serial reports offline twice in a row.
    /// </summary>
    private async Task EnsureEmulatorOfflineAsync(
        AndroidSdkLayout layout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(2);
        var consecutiveOffline = 0;
        while (true)
        {
            var state = await _runner.RunAsync(
                AndroidCommandFactory.Adb(layout, _options, "get-state"),
                cancellationToken);
            var online = state.ExitCode == 0 &&
                string.Equals(state.StandardOutput.Trim(), "device", StringComparison.OrdinalIgnoreCase);
            if (!online)
            {
                consecutiveOffline++;
                if (consecutiveOffline >= 2) return;
            }
            else
            {
                consecutiveOffline = 0;
                if (DateTimeOffset.UtcNow > deadline)
                {
                    throw new TimeoutException("冷重启验证前未能完全停止安卓虚拟机。");
                }
                await _runner.RunAsync(AndroidCommandFactory.StopEmulator(layout, _options), cancellationToken);
            }

            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
        }
    }

    private async Task<string> DownloadAsync(
        PinnedDependency dependency,
        CancellationToken cancellationToken)
    {
        var destination = Path.Combine(_paths.DownloadCache, dependency.FileName);
        if (File.Exists(destination))
        {
            var hash = await Security.Sha256Verifier.ComputeAsync(destination, cancellationToken);
            if (hash.Equals(dependency.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return destination;
            }
        }

        await _downloader.DownloadAsync(dependency.Source, destination, dependency.Sha256, cancellationToken);
        return destination;
    }

    private string CreateStagingDirectory(string component)
    {
        var path = Path.Combine(_paths.RuntimeRoot, "staging", $"{component}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string GetSdkManagerPath(AndroidSdkLayout layout) =>
        Path.Combine(layout.Root, "cmdline-tools", "latest", "bin", "sdkmanager.bat");

    private IReadOnlyDictionary<string, string> CreateSdkEnvironment(AndroidSdkLayout layout)
    {
        var inheritedPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        return new Dictionary<string, string>(AndroidEmulatorEnvironment.Create(layout, _options), StringComparer.OrdinalIgnoreCase)
        {
            ["JAVA_HOME"] = _paths.JavaHome,
            ["PATH"] = string.Join(Path.PathSeparator,
                Path.Combine(_paths.JavaHome, "bin"),
                Path.Combine(layout.Root, "platform-tools"),
                inheritedPath)
        };
    }

    private static void Report(IProgress<SetupProgressState>? progress, SetupStage stage) =>
        progress?.Report(SetupProgressCatalog.All.Single(state => state.Stage == stage));

    private static void EnsureSuccess(ProcessResult result, string operation)
    {
        if (result.ExitCode == 0) return;
        var combined = string.Join(
            Environment.NewLine,
            new[] { result.StandardOutput, result.StandardError }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim()));
        var detail = combined.Length > 6000 ? combined[^6000..] : combined;
        throw new InvalidOperationException($"{operation}失败：{detail}");
    }

    private InstallJournalStore Journal =>
        new(Path.Combine(_paths.ProductRoot, "install-state.json"));

    private sealed class SetupDownloadProgress(IProgress<SetupProgressState> progress, long totalBytes)
        : IProgress<DownloadProgress>
    {
        private string? _component;
        private long _currentReceived;
        private long _completed;
        private long _lastReported;
        private long _lastTick;
        private int _lastPercent = 25;

        public static IProgress<DownloadProgress>? Create(IProgress<SetupProgressState>? progress)
        {
            if (progress is null) return null;
            var total = DependencyDownloadCatalog.Required().Sum(item => item.Size);
            return new SetupDownloadProgress(progress, total);
        }

        public void Report(DownloadProgress value)
        {
            if (value.Notice is not null)
            {
                progress.Report(new SetupProgressState(SetupStage.Download, _lastPercent, "下载运行环境",
                    $"{value.Component}：{value.Notice}", value));
                return;
            }
            if (!string.Equals(_component, value.Component, StringComparison.Ordinal))
            {
                _completed += _currentReceived;
                _component = value.Component;
                _currentReceived = 0;
                _lastReported = 0;
            }
            _currentReceived = value.ReceivedBytes;
            var received = _completed + _currentReceived;
            var complete = value.TotalBytes > 0 && value.ReceivedBytes >= value.TotalBytes;
            var now = Environment.TickCount64;
            if (!complete && received - _lastReported < 1024 * 1024 && now - _lastTick < 250) return;
            _lastReported = received;
            _lastTick = now;
            var percent = totalBytes <= 0 ? 25 : 10 + (int)Math.Min(40, 40.0 * received / totalBytes);
            _lastPercent = percent;
            progress.Report(new SetupProgressState(SetupStage.Download, percent, "下载运行环境",
                $"{value.Component}：{received / (1024d * 1024):F0}/{totalBytes / (1024d * 1024):F0} MB", value));
        }
    }

}
