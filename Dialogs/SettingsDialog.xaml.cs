using System.IO;
using System.Windows;
using System.Windows.Input;
using GreenLuma_Manager.Models;
using GreenLuma_Manager.Services;
using GreenLuma_Manager.Utilities;
using Microsoft.Win32;

namespace GreenLuma_Manager.Dialogs;

public partial class SettingsDialog
{
    private readonly Config _config;

    public SettingsDialog(Config config)
    {
        InitializeComponent();

        _config = config;

        LoadSettings();
        UpdateAutoUpdateVisibility();

        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void LoadSettings()
    {
        TxtSteamPath.Text = _config.SteamPath;
        TxtGreenLumaPath.Text = _config.GreenLumaPath;
        ChkReplaceSteamAutostart.IsChecked = _config.ReplaceSteamAutostart;
        ChkDisableUpdateCheck.IsChecked = _config.DisableUpdateCheck;
        ChkAutoUpdate.IsChecked = _config.AutoUpdate;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Cancel_Click(this, new RoutedEventArgs());
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void BrowseSteam_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select Steam folder (e.g., C:\\Program Files (x86)\\Steam)"
        };

        if (!string.IsNullOrWhiteSpace(TxtSteamPath.Text) && Directory.Exists(TxtSteamPath.Text))
            dialog.InitialDirectory = TxtSteamPath.Text;

        if (dialog.ShowDialog() == true) TxtSteamPath.Text = dialog.FolderName;
    }

    private void BrowseGreenLuma_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select GreenLuma folder (containing DLLInjector.exe)"
        };

        if (!string.IsNullOrWhiteSpace(TxtGreenLumaPath.Text) && Directory.Exists(TxtGreenLumaPath.Text))
            dialog.InitialDirectory = TxtGreenLumaPath.Text;

        if (dialog.ShowDialog() == true) TxtGreenLumaPath.Text = dialog.FolderName;
    }

    private void AutoDetect_Click(object sender, RoutedEventArgs e)
    {
        var (steamPath, greenLumaPath) = PathDetector.DetectPaths();

        TxtSteamPath.Text = steamPath;
        TxtGreenLumaPath.Text = greenLumaPath;

        if (!string.IsNullOrWhiteSpace(steamPath) && !string.IsNullOrWhiteSpace(greenLumaPath))
            CustomMessageBox.Show("Paths detected successfully!", "Success", icon: MessageBoxImage.Asterisk);
        else
            CustomMessageBox.Show("Could not detect all paths automatically. Please set them manually.",
                "Detection", icon: MessageBoxImage.Exclamation);
    }

    private void WipeData_Click(object sender, RoutedEventArgs e)
    {
        var confirm1 = CustomMessageBox.Show(
            "This will delete all profiles and settings. Continue?",
            "Wipe Data",
            MessageBoxButton.YesNo,
            MessageBoxImage.Exclamation);

        if (confirm1 != MessageBoxResult.Yes)
            return;

        var confirm2 = CustomMessageBox.Show(
            "Are you absolutely sure? This cannot be undone.",
            "Confirm Wipe",
            MessageBoxButton.YesNo,
            MessageBoxImage.Exclamation);

        if (confirm2 != MessageBoxResult.Yes)
            return;

        ConfigService.WipeData();

        CustomMessageBox.Show("All data has been wiped. The application will now close.", "Complete",
            icon: MessageBoxImage.Asterisk);

        Application.Current.Shutdown();
    }

    private void DisableUpdateCheck_Changed(object sender, RoutedEventArgs e)
    {
        UpdateAutoUpdateVisibility();
    }

    private void UpdateAutoUpdateVisibility()
    {
        if (ChkAutoUpdate == null || ChkDisableUpdateCheck == null)
            return;

        var isEnabled = !ChkDisableUpdateCheck.IsChecked.GetValueOrDefault();
        ChkAutoUpdate.IsEnabled = isEnabled;

        if (!isEnabled) ChkAutoUpdate.IsChecked = false;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var steamPath = NormalizePath(TxtSteamPath.Text);
        var greenLumaPath = NormalizePath(TxtGreenLumaPath.Text);

        if (!ValidatePaths(steamPath, greenLumaPath))
            return;

        _config.SteamPath = steamPath;
        _config.GreenLumaPath = greenLumaPath;
        _config.ReplaceSteamAutostart = ChkReplaceSteamAutostart.IsChecked.GetValueOrDefault();
        _config.DisableUpdateCheck = ChkDisableUpdateCheck.IsChecked.GetValueOrDefault();
        _config.AutoUpdate = ChkAutoUpdate.IsChecked.GetValueOrDefault();

        ConfigService.Save(_config);
        AutostartManager.ManageAutostart(_config.ReplaceSteamAutostart, _config);

        DialogResult = true;
        Close();
    }

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        return path.Trim().TrimEnd('\\', '/');
    }

    private static bool ValidatePaths(string steamPath, string greenLumaPath)
    {
        if (string.IsNullOrWhiteSpace(steamPath))
        {
            CustomMessageBox.Show("Steam path cannot be empty.", "Validation", icon: MessageBoxImage.Exclamation);
            return false;
        }

        if (string.IsNullOrWhiteSpace(greenLumaPath))
        {
            CustomMessageBox.Show("GreenLuma path cannot be empty.", "Validation",
                icon: MessageBoxImage.Exclamation);
            return false;
        }

        if (!Directory.Exists(steamPath))
        {
            CustomMessageBox.Show("Steam path does not exist.", "Validation", icon: MessageBoxImage.Exclamation);
            return false;
        }

        var steamExePath = Path.Combine(steamPath, "Steam.exe");
        if (!File.Exists(steamExePath))
        {
            CustomMessageBox.Show($"Steam.exe not found at:\n{steamExePath}", "Validation",
                icon: MessageBoxImage.Exclamation);
            return false;
        }

        if (!Directory.Exists(greenLumaPath))
        {
            CustomMessageBox.Show($"GreenLuma path does not exist:\n{greenLumaPath}", "Validation",
                icon: MessageBoxImage.Exclamation);
            return false;
        }

        var injectorPath = Path.Combine(greenLumaPath, "DLLInjector.exe");
        if (!File.Exists(injectorPath))
        {
            CustomMessageBox.Show($"DLLInjector.exe not found at:\n{injectorPath}", "Validation",
                icon: MessageBoxImage.Exclamation);
            return false;
        }

        return true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}