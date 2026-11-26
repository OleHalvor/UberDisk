using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using UberDisk.Core.Updates;

namespace UberDisk.App;

/// <summary>
/// Updates from GitHub releases: a quiet check at startup (can be turned off), a banner in the drive rail when a
/// newer release is out, and "Install and restart", which swaps in the release's UberDisk.exe and starts it.
/// </summary>
public partial class MainWindow
{
    private const string Repo = "OleHalvor/UberDisk";
    private ReleaseInfo? _update;
    private bool _checking, _installing;

    /// <summary>The running exe, if this is the one-file download that can replace itself (not a dev build).</summary>
    private static string? SelfExe
    {
        get
        {
            var exe = Environment.ProcessPath;
            if (exe == null || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
            // A dev or framework-dependent build has UberDisk.dll beside the exe; swapping only the exe would break it.
            return File.Exists(Path.Combine(AppContext.BaseDirectory, "UberDisk.dll")) ? null : exe;
        }
    }

    private void StartUpdateCheck()
    {
        if (Demo.On) return;
        if (SelfExe is { } exe)
        {
            _ = Updater.CleanupAsync(exe); // UberDisk.exe.old from the last update
            SyncInstalledVersion(exe);
        }
        if (!AppSettings.CheckUpdates) return;
        _ = Dispatcher.InvokeAsync(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(3)); // let the first scan get going
            await CheckForUpdateAsync(manual: false);
        });
    }

    private async Task CheckForUpdateAsync(bool manual)
    {
        if (_checking || _installing) return;
        _checking = true;
        try
        {
            using var http = Updater.NewClient(App.Version);
            var latest = await Updater.GetLatestAsync(http, Repo, CancellationToken.None);
            if (latest == null || !Updater.IsNewer(latest, App.Version))
            {
                if (manual)
                    MessageBox.Show(this, $"You have the latest version, UberDisk {App.Version}.", "UberDisk",
                                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (!manual && string.Equals(AppSettings.SkipVersion, latest.Tag, StringComparison.OrdinalIgnoreCase)) return;
            ShowUpdate(latest);
        }
        catch (Exception ex)
        {
            // At startup a failed check (offline, rate limit) is not worth a word.
            if (manual)
                MessageBox.Show(this, $"Couldn't check GitHub for updates.\n\n{ex.Message}", "UberDisk",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { _checking = false; }
    }

    private void ShowUpdate(ReleaseInfo release)
    {
        _update = release;
        string v = release.Tag.TrimStart('v', 'V');
        bool self = SelfExe != null && release.ExeUrl != null;
        UpdateTitle.Text = $"UberDisk {v} is available";
        UpdateDetail.Text = self
            ? $"You have {App.Version}. It downloads " + (release.ExeSize > 0 ? Core.Viz.Format.Bytes(release.ExeSize) + " " : "") +
              "from GitHub, then restarts UberDisk."
            : $"You have {App.Version}.";
        UpdateInstallBtn.Content = self ? "Install and restart" : "Download";
        UpdateInstallBtn.ToolTip = self ? "Replace this UberDisk.exe with the new one and restart" : "Open the release on GitHub";
        UpdateButtons.Visibility = UpdateLinks.Visibility = Visibility.Visible;
        UpdateProgress.Visibility = Visibility.Collapsed;
        UpdateBanner.Visibility = Visibility.Visible;
    }

    private async void UpdateInstall_Click(object sender, RoutedEventArgs e)
    {
        if (_update is not { } release || _installing) return;
        string? exe = SelfExe;
        if (exe == null || release.ExeUrl == null)
        {
            App.OpenUrl(release.PageUrl);
            return;
        }

        _installing = true;
        UpdateButtons.Visibility = UpdateLinks.Visibility = Visibility.Collapsed;
        UpdateProgress.Visibility = Visibility.Visible;
        UpdateProgressFill.Width = 0;
        UpdateDetail.Text = "Downloading…";
        string download = exe + Updater.DownloadSuffix;
        try
        {
            var progress = new Progress<double>(p =>
            {
                UpdateProgressFill.Width = Math.Max(0, UpdateProgress.ActualWidth * p);
                UpdateDetail.Text = $"Downloading… {p * 100:0} %";
            });
            using (var http = Updater.NewClient(App.Version))
                await Updater.DownloadAsync(http, release, download, progress, CancellationToken.None);

            UpdateDetail.Text = "Restarting…";
            Updater.Swap(exe, download);
        }
        catch (Exception ex)
        {
            _installing = false;
            try { File.Delete(download); } catch { /* cleaned up next start */ }
            ShowUpdate(release);
            string why = ex is UnauthorizedAccessException
                ? $"UberDisk isn't allowed to replace itself in {Path.GetDirectoryName(exe)}."
                : $"The update didn't install: {ex.Message}";
            var answer = MessageBox.Show(this, why + "\n\nOpen the release on GitHub to download it by hand?", "UberDisk",
                                         MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer == MessageBoxResult.Yes) App.OpenUrl(release.PageUrl);
            return;
        }

        // The new exe is in place. Close this window first so settings and window place are saved, then start it.
        Close();
        try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! }); }
        catch (Exception ex)
        {
            MessageBox.Show($"UberDisk {release.Tag.TrimStart('v', 'V')} is installed, but didn't start: {ex.Message}\n\nStart it again from {exe}.",
                            "UberDisk", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>The uninstall entry UberDisk-Setup.exe writes (Inno Setup: AppId from installer\UberDisk.iss + "_is1").</summary>
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{C58BCDC3-D240-45E9-9853-868DAF129E2A}_is1";

    /// <summary>
    /// An installed copy updates itself by swapping its exe, like the portable one, so the version that Settings &gt;
    /// Apps shows would stay at the installed one. When this exe is the installed one, bring that entry up to date.
    /// </summary>
    private static void SyncInstalledVersion(string exe)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(UninstallKey, writable: true);
            if (key?.GetValue("InstallLocation") is not string dir) return;
            if (!string.Equals(Path.GetFullPath(dir).TrimEnd('\\'), Path.GetDirectoryName(exe), StringComparison.OrdinalIgnoreCase)) return;
            if (key.GetValue("DisplayVersion") as string != App.Version) key.SetValue("DisplayVersion", App.Version);
        }
        catch { /* only cosmetic */ }
    }

    private void UpdateLater_Click(object sender, RoutedEventArgs e) => UpdateBanner.Visibility = Visibility.Collapsed;

    private void UpdateNotes_Click(object sender, RoutedEventArgs e)
    {
        if (_update != null) App.OpenUrl(_update.PageUrl);
    }

    private void UpdateSkip_Click(object sender, RoutedEventArgs e)
    {
        if (_update == null) return;
        AppSettings.SkipVersion = _update.Tag;
        AppSettings.Save();
        UpdateBanner.Visibility = Visibility.Collapsed;
    }

    /// <summary>The version button in the rail: check now, the startup-check switch, and the releases page.</summary>
    private void Version_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        if (!Demo.On)
        {
            menu.Items.Add(Item("", "Check for updates now", "", () => _ = CheckForUpdateAsync(manual: true)));
            menu.Items.Add(Item(AppSettings.CheckUpdates ? "" : "", "Check for updates at startup", "", () =>
            {
                AppSettings.CheckUpdates = !AppSettings.CheckUpdates;
                AppSettings.Save();
            }));
            menu.Items.Add(new Separator());
        }
        menu.Items.Add(Item("", "Releases on GitHub", "", () => App.OpenUrl(App.RepoUrl + "/releases")));
        menu.PlacementTarget = (UIElement)sender;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        menu.IsOpen = true;
    }
}
