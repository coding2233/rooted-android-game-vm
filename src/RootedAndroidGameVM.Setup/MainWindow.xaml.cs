using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Navigation;
using System.Windows.Threading;
using Microsoft.Win32;
using RootedAndroidGameVM.Core.Downloads;
using RootedAndroidGameVM.Core.Setup;
using RootedAndroidGameVM.Core.Security;
using RootedAndroidGameVM.Core.Ui;
using RootedAndroidGameVM.Core.Storage;
using RootedAndroidGameVM.Core.Android;

namespace RootedAndroidGameVM.Setup;

public partial class MainWindow : Window
{
    private CancellationTokenSource? _installationCancellation;
    private bool _installationSucceeded;
    private readonly ProductStorageLocation _location;
    private readonly bool _createShortcuts;
    private readonly int _port;
    private bool _programUpdateOnly;
    private readonly ObservableCollection<ProgressRow> _rows = [];
    private readonly ObservableCollection<DownloadLinkRow> _linkRows = [];
    private readonly Dictionary<string, ProgressRow> _rowByFile = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private ProgressRow? _currentRow;
    private DateTimeOffset? _startedAt;
    private long _lastReceived;
    private long _lastTick;
    private long _lastDataTick;
    private double _lastSpeed;

    public MainWindow(ProductStorageLocation? location = null, bool createShortcuts = true, int port = 5554, bool programUpdate = false)
    {
        _location = location ?? new ProductStorageLocation();
        _createShortcuts = createShortcuts;
        _port = port;
        InitializeComponent();
        StageList.ItemsSource = SetupProgressCatalog.All
            .Where(state => state.Stage != SetupStage.Complete)
            .Select((state, index) => new
            {
                Number = (index + 1).ToString(),
                state.Title,
                Caption = state.Detail
            });
        ProgressList.ItemsSource = _rows;
        DownloadLinkList.ItemsSource = _linkRows;
        _timer.Tick += (_, _) => { UpdateStallIndicator(); UpdateElapsedText(); };
        Closing += MainWindow_Closing;
        RefreshResourceSelection();
        Loaded += async (_, _) => { await RefreshDownloadStatusAsync(); await BuildProgressRowsAsync(); };
        if (programUpdate)
        {
            InstallButton.IsEnabled = false;
            Loaded += async (_, _) => await CheckProgramUpgradeAsync();
        }
    }

    private async Task CheckProgramUpgradeAsync()
    {
        try
        {
            using var lease = StorageOperationLease.Acquire(_location);
            _programUpdateOnly = await ProgramUpgradeProbe.CanReuseAsync(
                InstallPaths.FromProductRoot(_location.ReadRoot(), new InstallPathConfigurationStore(_location.ControlRoot).Read()));
            if (!_programUpdateOnly) return;
            HeadingText.Text = "程序已更新";
            SubtitleText.Text = "已识别原有运行环境，可以继续使用同一个模拟器。";
            ProgressTitleText.Text = "现有资源已保留";
            ProgressDetailText.Text = "无需重新下载或创建模拟器。资源位置和安卓应用数据保持不变。";
            ProgressPercentText.Text = "100%";
            InstallProgressBar.Value = 100;
            InstallButton.Content = "完成更新";
            LicenseCheckBox.Visibility = Visibility.Collapsed;
            LicenseLinkText.Visibility = Visibility.Collapsed;
        }
        catch (Exception exception) { ProgressDetailText.Text = LogRedactor.RedactLocalPaths(exception.Message); }
        finally { InstallButton.IsEnabled = true; }
    }

    private void RefreshResourceSelection()
    {
        var root = _location.ReadRoot();
        ResourcePathTextBox.Text = root;
        SdkPathTextBox.Text = new InstallPathConfigurationStore(_location.ControlRoot).Read().SdkRoot ?? string.Empty;
        var existingResources = Directory.Exists(root) && StorageOwnership.IsOwned(root);
        ResourcePathTextBox.IsReadOnly = existingResources;
        BrowseResourceButton.IsEnabled = !existingResources;
        if (!existingResources) return;
        HeadingText.Text = "更新或修复运行环境";
        InstallButton.Content = "更新并验证";
        StorageDescriptionText.Text = "更新会保留这个目录中的模拟器和应用数据。需要更换磁盘时，请在启动器的“资源位置”中迁移。";
        ProgressDetailText.Text = "沿用现有资源，检查所需组件和 Root。";
    }

    private void BrowseResource_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择资源存储文件夹", Multiselect = false };
        if (dialog.ShowDialog(this) == true) ResourcePathTextBox.Text = dialog.FolderName;
    }

    private void BrowseSdk_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择 Android SDK 文件夹", Multiselect = false };
        if (dialog.ShowDialog(this) == true) SdkPathTextBox.Text = dialog.FolderName;
    }

    private string ResolveDownloadCache()
    {
        var root = ResourcePathTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(root)) root = _location.ReadRoot();
        var configuration = new InstallPathConfigurationStore(_location.ControlRoot).Read();
        return InstallPaths.FromProductRoot(root, configuration).DownloadCache;
    }

    private async Task RefreshDownloadStatusAsync()
    {
        try
        {
            var store = new DownloadSourceConfigurationStore(_location.ControlRoot);
            var configuration = store.Read();
            GoogleMirrorTextBox.Text = RuleFor(configuration, "https://dl.google.com/android/repository/");
            GitHubMirrorTextBox.Text = RuleFor(configuration, "https://github.com/");
            MicrosoftMirrorTextBox.Text = RuleFor(configuration, "https://download.visualstudio.microsoft.com/");
            var states = await DependencyDownloadCatalog.InspectAsync(ResolveDownloadCache(), configuration);
            var cached = states.Count(state => state.Verified);
            DownloadStatusText.Text = $"需要 {states.Count} 个组件，已缓存并校验 {cached} 个。缺失的组件可用浏览器/下载器下载后，放入同一文件夹并点击“从本地文件夹导入组件”。";
        }
        catch (Exception error)
        {
            DownloadStatusText.Text = LogRedactor.RedactLocalPaths(error.Message);
        }
    }

    private static string RuleFor(DownloadSourceConfiguration configuration, string from) =>
        configuration.Rules.FirstOrDefault(rule =>
            rule.From.Equals(from.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))?.To ?? string.Empty;

    private async Task BuildProgressRowsAsync()
    {
        try
        {
            var configuration = new DownloadSourceConfigurationStore(_location.ControlRoot).Read();
            var states = await DependencyDownloadCatalog.InspectAsync(ResolveDownloadCache(), configuration);
            _rows.Clear();
            _rowByFile.Clear();
            _linkRows.Clear();
            foreach (var state in states)
            {
                var row = new ProgressRow(state.Item.Name, state.Item.ArchiveFileName, state.Item.Size);
                if (state.Verified)
                {
                    row.Status = "已缓存";
                    row.Percent = 100;
                    row.Detail = $"{FormatMb(state.Item.Size)} MB";
                }
                _rows.Add(row);
                _rowByFile[state.Item.ArchiveFileName] = row;
                _linkRows.Add(new DownloadLinkRow(
                    state.Item.Name, state.Item.ArchiveFileName, state.Item.Url, state.Item.Size, state.Verified));
            }
            CachePathText.Text = "下载缓存：" + ResolveDownloadCache();
        }
        catch (Exception error)
        {
            StageText.Text = LogRedactor.RedactLocalPaths(error.Message);
        }
    }

    private async Task SaveMirrorAsync()
    {
        var rules = new List<DownloadSourceRule>();
        void Add(string from, string to)
        {
            if (!string.IsNullOrWhiteSpace(to)) rules.Add(DownloadSourceRule.Create(from, to.Trim()));
        }
        Add("https://dl.google.com/android/repository/", GoogleMirrorTextBox.Text);
        Add("https://github.com/", GitHubMirrorTextBox.Text);
        Add("https://download.visualstudio.microsoft.com/", MicrosoftMirrorTextBox.Text);
        var store = new DownloadSourceConfigurationStore(_location.ControlRoot);
        if (rules.Count == 0) { store.Clear(); return; }
        await store.SaveAsync(DownloadSourceConfiguration.Create(rules));
    }

    private async void ImportDownloads_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择包含已下载组件的文件夹", Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        ImportDownloadsButton.IsEnabled = false;
        try
        {
            var results = await new DependencyImportService().ImportAsync(dialog.FolderName, ResolveDownloadCache());
            var verified = results.Count(item => item.Outcome is "imported" or "already-cached");
            await RefreshDownloadStatusAsync();
            var detail = string.Join(Environment.NewLine, results.Select(item => $"{item.Outcome}：{item.ArchiveFileName}"));
            MessageBox.Show(this, $"已校验 {verified}/{results.Count} 个组件。\n\n{detail}", "导入结果",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception error)
        {
            MessageBox.Show(this, LogRedactor.RedactLocalPaths(error.Message), "导入失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ImportDownloadsButton.IsEnabled = true;
        }
    }

    private async void ClearMirror_Click(object sender, RoutedEventArgs e)
    {
        new DownloadSourceConfigurationStore(_location.ControlRoot).Clear();
        await RefreshDownloadStatusAsync();
    }

    private async void ChinaPreset_Click(object sender, RoutedEventArgs e)
    {
        var preset = DownloadMirrorPresets.China;
        GoogleMirrorTextBox.Text = PresetRule(preset, "https://dl.google.com/android/repository/");
        GitHubMirrorTextBox.Text = PresetRule(preset, "https://github.com/");
        MicrosoftMirrorTextBox.Text = string.Empty;
        await SaveMirrorAsync();
        await RefreshDownloadStatusAsync();
        MessageBox.Show(this,
            "已填入国内镜像：Google 走腾讯云 AndroidSDK，GitHub 走 ghfast。\n\nghfast 不可用时，可把 GitHub 前缀改为 https://gh-proxy.com/https://github.com/ 。\nMicrosoft JDK 无国内镜像，保持直连或改用“从本地文件夹导入组件”。\n\n所有下载仍按 SHA-256 校验。",
            "国内镜像", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static string PresetRule(DownloadMirrorPreset preset, string from) =>
        preset.Rules.FirstOrDefault(rule =>
            rule.From.Equals(from.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))?.To ?? string.Empty;

    private void CopyLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string url } && !string.IsNullOrWhiteSpace(url))
        {
            try { Clipboard.SetText(url); DownloadStatusText.Text = "已复制链接：" + url; }
            catch (Exception error) { DownloadStatusText.Text = error.Message; }
        }
    }

    private void OpenLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string url } && !string.IsNullOrWhiteSpace(url))
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
    }

    private void CopyAllLinks_Click(object sender, RoutedEventArgs e)
    {
        var text = string.Join(Environment.NewLine + Environment.NewLine,
            _linkRows.Select(row => $"{row.Name}{Environment.NewLine}{row.FileName}{Environment.NewLine}{row.Url}"));
        try { Clipboard.SetText(text); DownloadStatusText.Text = "已复制全部组件的下载链接。"; }
        catch (Exception error) { DownloadStatusText.Text = error.Message; }
    }

    private void OpenCache_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var cache = ResolveDownloadCache();
            Directory.CreateDirectory(cache);
            Process.Start(new ProcessStartInfo(cache) { UseShellExecute = true });
        }
        catch (Exception error) { DownloadStatusText.Text = LogRedactor.RedactLocalPaths(error.Message); }
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_programUpdateOnly)
        {
            if (_createShortcuts)
                ShortcutService.CreateLauncherStartMenuShortcut(Path.Combine(AppContext.BaseDirectory, "RootedAndroidGameVM.exe"));
            _installationSucceeded = true;
            Close();
            return;
        }
        if (LicenseCheckBox.IsChecked != true)
        {
            MessageBox.Show(this, "请先勾选接受 Android SDK 许可协议。", "需要接受许可",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _installationCancellation = new CancellationTokenSource();
        InstallButton.IsEnabled = false;
        LicenseCheckBox.IsEnabled = false;
        ExitButton.Content = "取消";
        _startedAt = DateTimeOffset.UtcNow;
        _currentRow = null;
        _lastSpeed = 0;
        _lastTick = 0;
        _lastDataTick = 0;
        foreach (var row in _rows)
            if (row.Status != "已缓存") { row.Status = "等待"; row.Percent = 0; row.Detail = ""; }
        _timer.Start();
        var progress = new Progress<SetupProgressState>(ApplyProgress);
        try
        {
            await SaveMirrorAsync();
            using var lease = StorageOperationLease.Acquire(_location);
            var selectedRoot = ResourcePathTextBox.Text.Trim();
            await StorageOwnership.InitializeAsync(selectedRoot, _location, _installationCancellation.Token);
            ResourcePathTextBox.IsReadOnly = true;
            BrowseResourceButton.IsEnabled = false;
            var pathService = new InstallPathConfigurationService(_location.ControlRoot);
            var existingConfiguration = pathService.Read();
            await pathService.ConfigureAsync(
                string.IsNullOrWhiteSpace(SdkPathTextBox.Text) ? null : SdkPathTextBox.Text.Trim(),
                existingConfiguration.AvdHome,
                existingConfiguration.DownloadCache,
                _installationCancellation.Token);
            var paths = InstallPaths.FromProductRoot(selectedRoot, pathService.Read());
            var options = AndroidVmOptions.ForPaths(paths) with { Port = _port, Serial = $"emulator-{_port}" };
            await new RootedVmInstaller(paths, options: options).InstallAsync(
                sdkLicenseAccepted: true,
                progress,
                _installationCancellation.Token);
            if (_createShortcuts)
                ShortcutService.CreateLauncherStartMenuShortcut(
                    Path.Combine(AppContext.BaseDirectory, "RootedAndroidGameVM.exe"));
            _installationSucceeded = true;
            InstallButton.Content = "安装完成";
            ProgressTitleText.Text = "安装完成";
            ProgressDetailText.Text = "Root、ADB 与虚拟机启动均已通过验证。现在可以关闭安装器。";
            MessageBox.Show(this, "安装与 Root 验证已完成。以后直接双击桌面启动器即可。",
                "安装完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            ProgressTitleText.Text = "安装已取消";
            ProgressDetailText.Text = "已保留完成下载的校验缓存，下次可以继续。";
            InstallButton.Content = "继续安装";
            InstallButton.IsEnabled = true;
            LicenseCheckBox.IsEnabled = true;
        }
        catch (Exception exception)
        {
            var message = LogRedactor.RedactLocalPaths(exception.Message);
            ProgressTitleText.Text = "安装未完成";
            ProgressDetailText.Text = message;
            InstallButton.Content = "重试";
            InstallButton.IsEnabled = true;
            LicenseCheckBox.IsEnabled = true;
            MessageBox.Show(this, message, "安装失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _installationCancellation?.Dispose();
            _installationCancellation = null;
            _timer.Stop();
            UpdateElapsedText();
            ExitButton.Content = "退出";
            ExitButton.IsEnabled = true;
        }
    }

    private void ApplyProgress(SetupProgressState state)
    {
        ProgressTitleText.Text = state.Title;
        ProgressDetailText.Text = state.Detail;
        ProgressPercentText.Text = $"{state.Percent}%";
        InstallProgressBar.Value = state.Percent;
        StageText.Text = StageLabel(state.Stage);
        if (state.Download is { } download && _rowByFile.TryGetValue(download.Component, out var row))
        {
            if (!ReferenceEquals(_currentRow, row))
            {
                if (_currentRow is { Status: "下载中" or "重试中" })
                {
                    _currentRow.Status = "完成";
                    _currentRow.Percent = 100;
                }
                _currentRow = row;
                _lastReceived = 0;
                _lastTick = 0;
            }
            if (download.Notice is not null)
            {
                row.Status = "重试中";
                row.Detail = download.Notice;
            }
            else
            {
                _lastDataTick = Environment.TickCount64;
                row.Status = "下载中";
                row.Percent = download.TotalBytes > 0
                    ? Math.Min(100, 100d * download.ReceivedBytes / download.TotalBytes)
                    : row.Percent;
                row.Detail = download.TotalBytes > 0
                    ? $"{FormatMb(download.ReceivedBytes)} / {FormatMb(download.TotalBytes)} MB"
                    : $"{FormatMb(download.ReceivedBytes)} MB";
                UpdateSpeed(download);
            }
        }
        if (state.Stage == SetupStage.Complete && _currentRow is not null)
        {
            _currentRow.Status = "完成";
            _currentRow.Percent = 100;
        }
    }

    private void UpdateStallIndicator()
    {
        if (_currentRow is null || _lastDataTick == 0 || _currentRow.Status != "下载中") return;
        var idle = Environment.TickCount64 - _lastDataTick;
        if (idle >= 5000) _currentRow.Status = $"停滞 {idle / 1000}s";
    }

    private void UpdateSpeed(DownloadProgress download)
    {
        var now = Environment.TickCount64;
        if (_lastTick == 0) { _lastTick = now; _lastReceived = download.ReceivedBytes; return; }
        var elapsed = now - _lastTick;
        if (elapsed < 400) return;
        var delta = download.ReceivedBytes - _lastReceived;
        if (delta >= 0) _lastSpeed = delta / (elapsed / 1000.0);
        _lastReceived = download.ReceivedBytes;
        _lastTick = now;
        UpdateElapsedText();
    }

    private void UpdateElapsedText()
    {
        if (_startedAt is null) { ElapsedText.Text = string.Empty; return; }
        var span = DateTimeOffset.UtcNow - _startedAt.Value;
        var speed = _lastSpeed > 0 ? $" · {FormatMb((long)_lastSpeed)} MB/s" : string.Empty;
        ElapsedText.Text = $"耗时 {span:mm\\:ss}{speed}";
    }

    private static string StageLabel(SetupStage stage) => stage switch
    {
        SetupStage.Preflight => "阶段 1/6 · 检查电脑",
        SetupStage.Download => "阶段 2/6 · 下载运行环境",
        SetupStage.CreateAvd => "阶段 3/6 · 创建虚拟机",
        SetupStage.Root => "阶段 4/6 · 配置 Root",
        SetupStage.Verify => "阶段 5/6 · 最终验证",
        SetupStage.Complete => "阶段 6/6 · 安装完成",
        _ => string.Empty
    };

    private static string FormatMb(long bytes) => (bytes / (1024d * 1024)).ToString("F0");

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        if (_installationCancellation is not null)
        {
            _installationCancellation.Cancel();
            ExitButton.IsEnabled = false;
            ProgressDetailText.Text = "正在安全停止当前步骤…";
            return;
        }

        Close();
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_installationCancellation is null) return;
        e.Cancel = true;
        _installationCancellation.Cancel();
        ExitButton.IsEnabled = false;
        ProgressDetailText.Text = "正在安全停止当前步骤…";
    }

    protected override void OnClosed(EventArgs e)
    {
        Environment.ExitCode = _installationSucceeded ? 0 : 1;
        base.OnClosed(e);
    }

    private void LicenseLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    public sealed class ProgressRow(string name, string fileName, long size) : INotifyPropertyChanged
    {
        private string _detail = string.Empty;
        private string _status = "等待";
        private double _percent;

        public string Name { get; } = name;
        public string FileName { get; } = fileName;
        public long Size { get; } = size;

        public string Detail { get => _detail; set { _detail = value; OnChanged(); } }
        public string Status { get => _status; set { _status = value; OnChanged(); } }
        public double Percent { get => _percent; set { _percent = value; OnChanged(); } }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged([CallerMemberName] string? property = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }

    public sealed class DownloadLinkRow(string name, string fileName, string url, long size, bool cached) : INotifyPropertyChanged
    {
        private string _status = cached ? "已缓存" : "缺失";

        public string Name { get; } = name;
        public string FileName { get; } = fileName;
        public string Url { get; } = url;
        public string SizeText { get; } = $"{size / (1024d * 1024):F0} MB";

        public string Status
        {
            get => _status;
            set { _status = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status))); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
