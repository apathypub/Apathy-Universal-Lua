using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Apathy.Editor;
using Apathy.Models;
using Apathy.Services;

namespace Apathy.Views;

public partial class LuaPage : UserControl
{
    public LuaPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
        // Loading a config changes which scripts are active; the editor can load/unload them too.
        SettingsStore.Replaced += (_, _) => Refresh();
        ScriptService.Changed += (_, _) => Refresh();
    }

    private ScriptEntry? Selected => ScriptList.SelectedItem as ScriptEntry;

    private void Refresh()
    {
        string? selectedName = Selected?.Name;
        var scripts = ScriptService.List();
        ScriptList.ItemsSource = scripts;
        ScriptList.SelectedItem = scripts.FirstOrDefault(s => s.Name == selectedName);
        UpdateDetails();
    }

    private void UpdateDetails()
    {
        var script = Selected;
        LoadButton.IsEnabled = ShareButton.IsEnabled = DeleteButton.IsEnabled = script != null;
        LoadButton.Content = script is { IsLoaded: true } ? "Unload script" : "Load script";

        if (script == null || !File.Exists(script.Path))
        {
            TitleValue.Text = "Select a script";
            BylineValue.Text = NameValue.Text = ModifiedValue.Text = StatusValue.Text = PermissionsValue.Text = "-";
            BylineValue.Text = "";
            DescriptionValue.Visibility = Visibility.Collapsed;
            RevokeButton.IsEnabled = false;
            return;
        }

        var manifest = ScriptManifest.Read(script.Path);
        TitleValue.Text = manifest.DisplayTitle(script.Name);
        BylineValue.Text = Byline(manifest);
        DescriptionValue.Text = manifest.Description ?? "";
        DescriptionValue.Visibility = string.IsNullOrWhiteSpace(manifest.Description) ? Visibility.Collapsed : Visibility.Visible;

        var file = new FileInfo(script.Path);
        NameValue.Text = file.Name;
        ModifiedValue.Text = file.LastWriteTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        StatusValue.Text = script.IsLoaded ? "Loaded" : "Not loaded";
        var granted = ScriptPermissions.GrantedTo(script.Name);
        // Show what it uses even before it's granted, so you know before running it.
        PermissionsValue.Text = manifest.Permissions.Count == 0 ? "none"
            : string.Join(", ", manifest.Permissions.Select(p => granted.Contains(p) ? p : p + " (ask)"));
        RevokeButton.IsEnabled = granted.Count > 0;
    }

    private static string Byline(ScriptManifest manifest)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(manifest.Author)) parts.Add("by " + manifest.Author);
        if (!string.IsNullOrWhiteSpace(manifest.Version)) parts.Add("v" + manifest.Version);
        return string.Join("  ·  ", parts);
    }

    // Forget the grants (and stop the script, which is still running with them).
    private void Revoke_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } script) return;
        if (script.IsLoaded) ScriptService.Unload(script);
        ScriptPermissions.Revoke(script.Name);
        UpdateDetails();
    }

    private void ScriptList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateDetails();

    private void Load_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } script) return;

        if (script.IsLoaded) ScriptService.Unload(script);
        else ScriptService.Load(script);
        UpdateDetails();
    }

    private void Share_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } script) return;
        try
        {
            Clipboard.SetText(ScriptSharing.Export(script.Path));
            ShareStatus.Text = $"Copied \"{script.Name}\" to the clipboard. Send it to someone; they use Import.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or System.Runtime.InteropServices.ExternalException)
        {
            ShareStatus.Text = $"Couldn't copy the script: {ex.Message}";
        }
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        string text;
        try
        {
            text = Clipboard.ContainsText() ? Clipboard.GetText() : "";
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            ShareStatus.Text = "Couldn't read the clipboard; try again.";
            return;
        }
        if (string.IsNullOrWhiteSpace(text))
        {
            ShareStatus.Text = "Copy a shared script first, then Import.";
            return;
        }

        // Show what it is and what it will want before it lands in the folder.
        var manifest = ScriptManifest.Parse(text);
        string what = manifest.DisplayTitle("this script");
        string byline = Byline(manifest);
        string perms = manifest.Permissions.Count == 0
            ? "It asks for no special permissions."
            : "It will ask to: " + string.Join(", ", manifest.Permissions.Select(p =>
                ScriptPermissions.Descriptions.TryGetValue(p, out var d) ? d : p)) + ".";
        string message = $"Import \"{what}\"{(byline.Length > 0 ? $" ({byline})" : "")}?\n\n{perms}\n\nIt won't run until you load it.";

        if (ConfirmDialog.Ask(Window.GetWindow(this), "Import script", message, "Import", "Cancel") != true) return;
        try
        {
            string path = ScriptSharing.Import(text);
            Refresh();
            ScriptList.SelectedItem = (ScriptList.ItemsSource as IEnumerable<ScriptEntry>)?
                .FirstOrDefault(s => s.Path == path);
            ShareStatus.Text = $"Imported as {Path.GetFileName(path)}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShareStatus.Text = $"Couldn't save the script: {ex.Message}";
        }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } script) return;
        string fileName = System.IO.Path.GetFileName(script.Path);
        if (ConfirmDialog.Ask(Window.GetWindow(this), "Delete script",
                $"Delete {fileName}? This can't be undone.", "Delete", "Cancel") != true) return;
        try
        {
            ScriptService.Delete(script);
            ScriptPermissions.Revoke(script.Name);
            ShareStatus.Text = $"Deleted {fileName}.";
            Refresh();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShareStatus.Text = $"Couldn't delete the script: {ex.Message}";
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private void OpenEditor_Click(object sender, RoutedEventArgs e) =>
        (Window.GetWindow(this) as MainWindow)?.OpenEditor(Selected?.Path);

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(ScriptService.Folder);
        Process.Start(new ProcessStartInfo(ScriptService.Folder) { UseShellExecute = true });
    }

    private void Clear_Click(object sender, RoutedEventArgs e) => LuaLog.Instance.Clear();

    // Keep the newest log line in view as text is appended.
    private void LogText_SizeChanged(object sender, SizeChangedEventArgs e) => LogScroll.ScrollToBottom();
}
