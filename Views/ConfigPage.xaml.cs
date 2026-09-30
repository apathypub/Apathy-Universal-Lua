using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Apathy.Models;
using Apathy.Services;

namespace Apathy.Views;

public partial class ConfigPage : UserControl
{
    public ConfigPage()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshList();
    }

    private string SelectedOrTypedName =>
        ConfigList.SelectedItem as string ?? NameBox.Text.Trim();

    private void RefreshList(string? select = null)
    {
        ConfigList.ItemsSource = ConfigService.List();
        if (select != null) ConfigList.SelectedItem = select;
    }

    private void ConfigList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ConfigList.SelectedItem is string name) NameBox.Text = name;
    }

    private void Load_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        string name = SelectedOrTypedName;
        SettingsStore.Replace(ConfigService.Load(name));
        return $"Loaded \"{name}\".";
    });

    private void Save_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        string name = NameBox.Text.Trim();
        if (!ConfigService.IsValidName(name)) return "Enter a valid config name.";

        ConfigService.Save(name, SettingsStore.Current);
        RefreshList(name);
        return $"Saved \"{name}\".";
    });

    private void Delete_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        if (ConfigList.SelectedItem is not string name) return "Select a config to delete.";

        ConfigService.Delete(name);
        RefreshList();
        return $"Deleted \"{name}\".";
    });

    private void Reset_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        SettingsStore.Replace(new MenuSettings());
        return "Settings reset to defaults.";
    });

    private void Import_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        SettingsStore.Replace(ConfigService.Deserialize(Clipboard.GetText()));
        return "Imported from clipboard.";
    });

    private void Export_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        Clipboard.SetText(ConfigService.Serialize(SettingsStore.Current));
        return "Copied to clipboard.";
    });

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        Directory.CreateDirectory(ConfigService.Folder);
        Process.Start(new ProcessStartInfo(ConfigService.Folder) { UseShellExecute = true });
        return "";
    });

    private void Run(Func<string> action)
    {
        try
        {
            StatusText.Text = action();
        }
        catch (Exception ex) when (ex is IOException or JsonException or ArgumentException
                                       or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException)
        {
            StatusText.Text = ex.Message;
        }
    }
}
