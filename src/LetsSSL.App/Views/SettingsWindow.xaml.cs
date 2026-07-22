using System;
using System.Windows;
using LetsSSL.App.ViewModels;
using Microsoft.Win32;

namespace LetsSSL.App.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _vm;

    public SettingsWindow(SettingsViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnBackup(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Back up LetsSSL4Windows configuration",
            Filter = "Backup archive (*.zip)|*.zip",
            FileName = $"LetsSSL4Windows-backup-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
        };
        if (dialog.ShowDialog(this) == true)
            _vm.Backup(dialog.FileName);
    }

    private void OnRestore(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Restore LetsSSL4Windows configuration",
            Filter = "Backup archive (*.zip)|*.zip",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != true) return;

        var confirm = MessageBox.Show(
            "Restoring will overwrite the current settings, managed certificates and ACME account keys with the contents of the backup. Continue?",
            "Restore from backup", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm == MessageBoxResult.Yes)
            _vm.Restore(dialog.FileName);
    }
}
