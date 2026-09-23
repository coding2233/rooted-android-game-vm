using System.Windows;
using Microsoft.Win32;
using RootedAndroidGameVM.Core.Security;
using RootedAndroidGameVM.Core.Setup;

namespace RootedAndroidGameVM.Launcher;

public partial class PathConfigurationWindow : Window
{
    private readonly bool _vmRunning;
    private readonly InstallPathConfigurationService _service = new();

    public PathConfigurationWindow(bool vmRunning = false)
    {
        _vmRunning = vmRunning;
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    private void Refresh()
    {
        var configuration = _service.Read();
        SdkPathTextBox.Text = configuration.SdkRoot ?? string.Empty;
        AvdPathTextBox.Text = configuration.AvdHome ?? string.Empty;
        CachePathTextBox.Text = configuration.DownloadCache ?? string.Empty;
        RunningNoticeText.Visibility = _vmRunning ? Visibility.Visible : Visibility.Collapsed;
        SaveButton.IsEnabled = !_vmRunning;
        try
        {
            var paths = _service.ResolvePaths();
            EffectiveText.Text = string.Join(Environment.NewLine,
                $"资源根：{paths.ProductRoot}",
                $"SDK：{paths.SdkRoot}（{(paths.SdkIsExternal ? "外部复用，只核验" : "产品自管")}）",
                $"AVD：{paths.AvdHome}（{(paths.AvdIsExternal ? "外部" : "产品自管")}）",
                $"下载缓存：{paths.DownloadCache}",
                $"配置文件：{_service.ConfigurationPath}");
        }
        catch (Exception error)
        {
            EffectiveText.Text = LogRedactor.RedactLocalPaths(error.Message);
        }
    }

    private void BrowseSdk_Click(object sender, RoutedEventArgs e) => PickFolder(SdkPathTextBox, "选择 Android SDK 文件夹");
    private void BrowseAvd_Click(object sender, RoutedEventArgs e) => PickFolder(AvdPathTextBox, "选择虚拟机 (AVD) 文件夹");
    private void BrowseCache_Click(object sender, RoutedEventArgs e) => PickFolder(CachePathTextBox, "选择下载缓存文件夹");

    private void PickFolder(System.Windows.Controls.TextBox target, string title)
    {
        var dialog = new OpenFolderDialog { Title = title, Multiselect = false };
        if (dialog.ShowDialog(this) == true) target.Text = dialog.FolderName;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_vmRunning) return;
        SaveButton.IsEnabled = false;
        try
        {
            await _service.ConfigureAsync(Empty(SdkPathTextBox.Text), Empty(AvdPathTextBox.Text), Empty(CachePathTextBox.Text));
            Refresh();
            MessageBox.Show(this, "组件路径已保存。请重新启动安卓使配置生效。", "已保存",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception error)
        {
            MessageBox.Show(this, LogRedactor.RedactLocalPaths(error.Message), "无法保存",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SaveButton.IsEnabled = !_vmRunning;
        }
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (_vmRunning) return;
        if (MessageBox.Show(this, "将恢复为产品自管的默认路径。继续？", "恢复默认路径",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _service.Clear();
        Refresh();
    }

    private static string? Empty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
