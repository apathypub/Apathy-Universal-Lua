using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Apathy.Models;
using Apathy.Services;
using Anthropic.Exceptions;
using ICSharpCode.AvalonEdit.Rendering;
using ICSharpCode.AvalonEdit.Search;

namespace Apathy.Editor;

/// <summary>A file in the scripts folder, as listed in the explorer.</summary>
public sealed record ScriptFile(string Name, string Path, ScriptLanguage Language, bool IsLoaded);

/// <summary>
/// Code editor for the scripts folder: explorer, tabs, syntax highlighting for the Lua family,
/// find/replace, Run/Stop for .lua files (they run in apathy's Lua host) and an AI agent that
/// creates and edits the files itself. One instance at a time.
/// </summary>
public partial class EditorWindow : Window
{
    private static EditorWindow? _instance;

    private readonly ObservableCollection<EditorDocument> _documents = [];

    private readonly SearchHighlighter _highlighter = new();
    private readonly LuaCompletion _completion;
    private List<ISearchResult> _matches = [];
    private ISearchStrategy? _findStrategy;

    private readonly AiAssistant _ai = new();
    private readonly AiFileTools _aiTools;
    private readonly ObservableCollection<ChatEntry> _aiEntries = [];
    private CancellationTokenSource? _aiCts;
    private string? _aiKey = AiKeyStore.Load();

    private EditorWindow()
    {
        ScriptLanguages.RegisterHighlighting();
        InitializeComponent();

        TabList.ItemsSource = _documents;
        AiMessages.ItemsSource = _aiEntries;
        OutputLines.ItemsSource = LuaLog.Instance.Lines;
        _aiTools = new AiFileTools(ScriptService.Folder, FindOpen, OpenForAi);
        Code.TextArea.TextView.BackgroundRenderers.Add(_highlighter);
        // Hints for every Lua-family file; the apathy API only where it runs (.lua).
        _completion = new LuaCompletion(Code, this, () => Current?.Language?.Runs == true);
        Code.TextChanged += (_, _) =>
        {
            if (FindBar.IsVisible) UpdateMatches();
        };
        StyleEditor();
        UpdateAiKeyPanel();
        InitStudio();

        SourceInitialized += (_, _) => Native.DisableRoundedCorners(new WindowInteropHelper(this).Handle);
        StateChanged += (_, _) =>
        {
            bool maximized = WindowState == WindowState.Maximized;
            // A maximized WindowChrome window hangs past the screen edges by the resize border.
            Root.Margin = maximized ? new Thickness(7) : new Thickness(0);
            MaxButton.Content = maximized ? "" : "";
        };
        Activated += (_, _) => RefreshFiles();
        Code.TextArea.Caret.PositionChanged += (_, _) => UpdateCaret();
        ScriptService.Changed += ScriptService_Changed;
        Closing += Window_Closing;
        Closed += (_, _) =>
        {
            ScriptService.Changed -= ScriptService_Changed;
            _aiCts?.Cancel();
            _instance = null;
        };

        ShowDocument();
    }

    /// <summary>Shows the editor (creating it if needed) and optionally opens a file in a tab.</summary>
    public static void Open(string? path = null)
    {
        _instance ??= new EditorWindow();
        _instance.Show();
        if (_instance.WindowState == WindowState.Minimized) _instance.WindowState = WindowState.Normal;
        _instance.Activate();
        if (path != null) _instance.OpenFile(path);
    }

    /// <summary>Closes the editor before apathy exits; false when the user cancelled (unsaved changes).</summary>
    public static bool CloseForExit()
    {
        _instance?.Close();
        return _instance == null;
    }

    private EditorDocument? Current => TabList.SelectedItem as EditorDocument;

    private void StyleEditor()
    {
        var accent = ((SolidColorBrush)FindResource("Sk.Accent")).Color;
        var area = Code.TextArea;
        area.SelectionBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x55, accent.R, accent.G, accent.B)));
        area.SelectionBorder = null;
        area.SelectionForeground = null;
        area.SelectionCornerRadius = 2;
        area.Caret.CaretBrush = (Brush)FindResource("Sk.Text");
        area.TextView.CurrentLineBackground = Frozen(new SolidColorBrush(Color.FromRgb(0x19, 0x19, 0x19)));
        area.TextView.CurrentLineBorder = Frozen(new Pen(Brushes.Transparent, 0));

        Code.Options.HighlightCurrentLine = true;
        Code.Options.ConvertTabsToSpaces = true;
        Code.Options.IndentationSize = 4;
        Code.Options.EnableHyperlinks = false;
        Code.Options.EnableEmailHyperlinks = false;
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    // ---------------- Files ----------------

    private void RefreshFiles()
    {
        string? selected = (FileList.SelectedItem as ScriptFile)?.Path;
        List<ScriptFile> files = Directory.Exists(ScriptService.Folder)
            ? Directory.EnumerateFiles(ScriptService.Folder)
                .Select(path => (path, language: ScriptLanguages.ForPath(path)))
                .Where(f => f.language != null)
                .Select(f => new ScriptFile(Path.GetFileName(f.path), f.path, f.language!,
                    f.language!.Runs && ScriptService.IsLoaded(Path.GetFileNameWithoutExtension(f.path))))
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [];
        FileList.ItemsSource = files;
        FileList.SelectedItem = files.FirstOrDefault(f => f.Path == selected);
        FileCount.Text = files.Count.ToString();
    }

    private EditorDocument? FindOpen(string path) =>
        _documents.FirstOrDefault(d => string.Equals(d.Path, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>Opens (or switches to) a file's tab; null if it couldn't be read.</summary>
    private EditorDocument? OpenFile(string path)
    {
        var doc = FindOpen(path);
        if (doc == null)
        {
            try
            {
                doc = new EditorDocument(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                SetStatus($"Can't open {Path.GetFileName(path)}: {ex.Message}");
                return null;
            }
            var opened = doc;
            opened.PropertyChanged += (_, _) =>
            {
                if (opened == Current) UpdateState();
            };
            _documents.Add(opened);
        }
        TabList.SelectedItem = doc;
        return doc;
    }

    // The AI edits through the file's tab so its changes show up live and Ctrl+Z undoes them.
    private EditorDocument? OpenForAi(string path)
    {
        var doc = OpenFile(path);
        RefreshFiles();
        return doc;
    }

    private void CreateFile()
    {
        string name = NewName.Text.Trim();
        if (name.Length == 0)
        {
            NewName.Focus();
            return;
        }
        if (!Path.HasExtension(name)) name += ScriptLanguages.Default.Extension;
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            SetStatus("That file name isn't allowed.");
            return;
        }
        if (ScriptLanguages.ForPath(name) is not { } language)
        {
            SetStatus("Use .lua, .enma, .luau, .tl, .moon or .fnl.");
            return;
        }

        string path = Path.Combine(ScriptService.Folder, name);
        if (!File.Exists(path))
        {
            try
            {
                Directory.CreateDirectory(ScriptService.Folder);
                File.WriteAllText(path, language.Template);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                SetStatus($"Can't create {name}: {ex.Message}");
                return;
            }
            SetStatus($"Created {name}");
        }

        NewName.Clear();
        RefreshFiles();
        OpenFile(path);
    }

    private void DeleteSelected()
    {
        if (FileList.SelectedItem is not ScriptFile file) return;
        if (ConfirmDialog.Ask(this, "Delete script", $"Delete {file.Name}? This can't be undone.", "Delete", "Cancel") != true)
            return;

        if (FindOpen(file.Path) is { } open) RemoveTab(open);
        if (file.IsLoaded) ScriptService.Unload(new ScriptEntry(Path.GetFileNameWithoutExtension(file.Path), file.Path));

        try
        {
            File.Delete(file.Path);
            SetStatus($"Deleted {file.Name}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus($"Can't delete {file.Name}: {ex.Message}");
        }
        RefreshFiles();
    }

    // ---------------- Tabs ----------------

    private void ShowDocument()
    {
        var doc = Current;
        Code.Document = doc?.Document;
        Code.SyntaxHighlighting = ScriptLanguages.Highlighting(doc?.Language);
        Code.Visibility = doc == null ? Visibility.Collapsed : Visibility.Visible;
        EmptyHint.Visibility = doc == null ? Visibility.Visible : Visibility.Collapsed;
        if (doc == null) FindBar.Visibility = Visibility.Collapsed;
        else if (FindBar.Visibility == Visibility.Visible) UpdateMatches();
        UpdateState();
        UpdateCaret();
        RefreshDiagnostics();
        if (doc != null) Code.Focus();
    }

    /// <summary>Closes a tab, asking about unsaved changes; false when the user cancelled.</summary>
    private bool CloseTab(EditorDocument doc)
    {
        if (doc.IsDirty)
        {
            var answer = ConfirmDialog.Ask(this, "Unsaved changes", $"Save changes to {doc.Name}?", "Save", "Don't save", "Cancel");
            if (answer == null || (answer == true && !Save(doc))) return false;
        }
        RemoveTab(doc);
        return true;
    }

    private void RemoveTab(EditorDocument doc)
    {
        int index = _documents.IndexOf(doc);
        bool wasCurrent = doc == Current;
        _documents.Remove(doc);
        if (wasCurrent && _documents.Count > 0) TabList.SelectedIndex = Math.Min(index, _documents.Count - 1);
    }

    private bool Save(EditorDocument doc)
    {
        try
        {
            doc.Save();
            SetStatus($"Saved {doc.Name}");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus($"Can't save {doc.Name}: {ex.Message}");
            NotificationService.Show("Save failed", ex.Message, NotificationKind.Error);
            return false;
        }
    }

    /// <summary>Saves every changed tab; false if any failed.</summary>
    private bool SaveAll() => _documents.Where(d => d.IsDirty).ToList().Count(d => !Save(d)) == 0;

    // ---------------- Run ----------------

    private void Run()
    {
        if (Current is not { Language.Runs: true } doc) return;
        if (doc.IsDirty && !Save(doc)) return;

        // Run from the editor reloads, so changes take effect immediately.
        ScriptService.Load(new ScriptEntry(doc.ScriptName, doc.Path), reload: true);
        SetStatus(ScriptService.IsLoaded(doc.ScriptName) ? $"Running {doc.Name}" : $"{doc.Name} failed to start, see Output");
    }

    private void Stop()
    {
        if (Current is not { } doc || !ScriptService.IsLoaded(doc.ScriptName)) return;
        ScriptService.Unload(new ScriptEntry(doc.ScriptName, doc.Path));
        SetStatus($"Stopped {doc.Name}");
    }

    private void ScriptService_Changed(object? sender, EventArgs e)
    {
        RefreshFiles();
        UpdateState();
    }

    // ---------------- Find / replace ----------------

    private void OpenFind(bool replace)
    {
        if (Current == null) return;
        FindBar.Visibility = Visibility.Visible;
        if (replace) SetReplaceVisible(true);
        if (Code.SelectionLength > 0 && !Code.SelectedText.Contains('\n')) FindBox.Text = Code.SelectedText;

        if (replace && FindBox.Text.Length > 0) ReplaceBox.Focus();
        else
        {
            FindBox.Focus();
            FindBox.SelectAll();
        }
        UpdateMatches();
    }

    private void SetReplaceVisible(bool visible)
    {
        ReplaceRow.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        ReplaceToggle.Content = visible ? "" : "";
    }

    private void CloseFind()
    {
        FindBar.Visibility = Visibility.Collapsed;
        _matches = [];
        _findStrategy = null;
        Repaint(null);
        Code.Focus();
    }

    /// <summary>Searches the whole document again (after typing, an option change or an edit) and repaints.</summary>
    private void UpdateMatches()
    {
        _matches = [];
        _findStrategy = null;
        FindStatus.Foreground = (Brush)FindResource("Sk.TextMuted");
        if (Current != null && FindBox.Text.Length > 0)
        {
            try
            {
                _findStrategy = SearchStrategyFactory.Create(FindBox.Text, ignoreCase: MatchCase.IsChecked != true,
                    matchWholeWords: WholeWord.IsChecked == true, UseRegex.IsChecked == true ? SearchMode.RegEx : SearchMode.Normal);
                _matches = _findStrategy.FindAll(Code.Document, 0, Code.Document.TextLength).ToList();
            }
            catch (SearchPatternException)
            {
                Repaint(null);
                FindStatus.Text = "Bad regex";
                FindStatus.Foreground = (Brush)FindResource("Ed.Red");
                return;
            }
        }
        Repaint(_matches.FirstOrDefault(m => m.Offset == Code.SelectionStart && m.Length == Code.SelectionLength));
    }

    private void Repaint(ISearchResult? current)
    {
        _highlighter.Matches = _matches;
        _highlighter.Current = current;
        int index = current == null ? -1 : _matches.IndexOf(current);
        FindStatus.Text = FindBox.Text.Length == 0 ? ""
            : _matches.Count == 0 ? "No results"
            : index >= 0 ? $"{index + 1} of {_matches.Count}"
            : $"{_matches.Count} found";
        Code.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
    }

    /// <summary>Selects the next (or previous) match after the selection, wrapping around the document.</summary>
    private void FindNext(bool backwards, bool fromSelectionStart = false)
    {
        if (_matches.Count == 0) return;
        int start = Code.SelectionStart;
        int end = fromSelectionStart ? start : start + Code.SelectionLength;
        var match = backwards
            ? _matches.LastOrDefault(m => m.Offset < start) ?? _matches[^1]
            : _matches.FirstOrDefault(m => m.Offset >= end) ?? _matches[0];

        Code.Select(match.Offset, match.Length);
        var location = Code.Document.GetLocation(match.Offset);
        Code.ScrollTo(location.Line, location.Column);
        Repaint(match);
    }

    private void ReplaceCurrent()
    {
        if (_findStrategy == null) return;
        var current = _matches.FirstOrDefault(m => m.Offset == Code.SelectionStart && m.Length == Code.SelectionLength);
        if (current == null)
        {
            FindNext(backwards: false);
            return;
        }

        string replacement = current.ReplaceWith(ReplaceBox.Text);
        Code.Document.Replace(current.Offset, current.Length, replacement);
        Code.Select(current.Offset + replacement.Length, 0);
        UpdateMatches();
        FindNext(backwards: false);
    }

    private void ReplaceAll()
    {
        if (_findStrategy == null || _matches.Count == 0) return;
        int count = _matches.Count;
        using (Code.Document.RunUpdate()) // a single Ctrl+Z undoes all of it
        {
            foreach (var match in _matches.OrderByDescending(m => m.Offset).ToList())
                Code.Document.Replace(match.Offset, match.Length, match.ReplaceWith(ReplaceBox.Text));
        }
        UpdateMatches();
        SetStatus($"Replaced {count} match{(count == 1 ? "" : "es")}");
    }

    private void FindBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateMatches();
        FindNext(backwards: false, fromSelectionStart: true); // jump to the first match as you type
    }

    private void FindBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        FindNext(backwards: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
    }

    private void ReplaceBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Alt)) ReplaceAll();
        else ReplaceCurrent();
    }

    private void FindOption_Click(object sender, RoutedEventArgs e) => UpdateMatches();

    private void FindNext_Click(object sender, RoutedEventArgs e) => FindNext(backwards: false);

    private void FindPrevious_Click(object sender, RoutedEventArgs e) => FindNext(backwards: true);

    private void FindClose_Click(object sender, RoutedEventArgs e) => CloseFind();

    private void ReplaceToggle_Click(object sender, RoutedEventArgs e) =>
        SetReplaceVisible(ReplaceRow.Visibility != Visibility.Visible);

    private void Replace_Click(object sender, RoutedEventArgs e) => ReplaceCurrent();

    private void ReplaceAll_Click(object sender, RoutedEventArgs e) => ReplaceAll();

    // ---------------- AI assistant ----------------

    private void ToggleAi() => ShowAi(AiPanel.Visibility != Visibility.Visible);

    private void ShowAi(bool show)
    {
        ShowSide(show ? AiPanel : null, 360);
        if (!show) return;
        if (_aiKey == null) AiKeyBox.Focus();
        else AiInput.Focus();
    }

    private void UpdateAiKeyPanel()
    {
        AiKeyPanel.Visibility = _aiKey == null ? Visibility.Visible : Visibility.Collapsed;
        AiRemoveKeyButton.Visibility = _aiKey == null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>The request plus the open file / selection (and any extra context) for the model.</summary>
    private string BuildPrompt(string question, bool includeFile, string? extra = null)
    {
        var prompt = new StringBuilder(question);
        if (includeFile && Current is { } doc)
        {
            string fence = doc.Language?.Name.ToLowerInvariant() ?? "";
            prompt.Append($"\n\nOpen file: {doc.Name}\n```{fence}\n{doc.Document.Text}\n```");
            if (Code.SelectionLength > 0) prompt.Append($"\n\nSelected code:\n```{fence}\n{Code.SelectedText}\n```");
        }
        if (extra != null) prompt.Append("\n\n").Append(extra);
        return prompt.ToString();
    }

    private async void AskAi(string request, bool forceFile = false, string? extra = null)
    {
        request = request.Trim();
        if (_aiCts != null || request.Length == 0) return;
        if (_aiKey is not { } key)
        {
            ShowAi(true);
            UpdateAiKeyPanel();
            return;
        }

        string prompt = BuildPrompt(request, forceFile || AiIncludeFile.IsChecked == true, extra);
        _aiEntries.Add(new ChatEntry(ChatEntryKind.User, request));

        // "Working..." stays at the bottom while the agent reads and writes files.
        var working = new ChatEntry(ChatEntryKind.Ai, "Working...");
        _aiEntries.Add(working);
        ChatEntry? textEntry = null;
        void Show(ChatEntry entry)
        {
            _aiEntries.Remove(working);
            _aiEntries.Add(entry);
            _aiEntries.Add(working);
        }

        _aiCts = new CancellationTokenSource();
        AiSendButton.Content = "";
        AiSendButton.ToolTip = "Stop";
        try
        {
            bool refused = await _ai.AskAsync(key, prompt, _aiTools,
                text =>
                {
                    text = text.Trim();
                    if (text.Length == 0) return;
                    if (textEntry == null)
                    {
                        textEntry = new ChatEntry(ChatEntryKind.Ai, text);
                        Show(textEntry);
                    }
                    else
                    {
                        textEntry.Text += "\n\n" + text;
                    }
                },
                result =>
                {
                    textEntry = null; // text after a file action starts a new block
                    Show(new ChatEntry(ChatEntryKind.Action, result.Action, result.FilePath));
                    if (!result.IsError) SetStatus($"AI: {result.Action}");
                },
                _aiCts.Token);

            if (refused) Show(new ChatEntry(ChatEntryKind.Error, "The model declined this request."));
        }
        catch (OperationCanceledException)
        {
            Show(new ChatEntry(ChatEntryKind.Error, "Stopped."));
        }
        catch (AnthropicUnauthorizedException)
        {
            Show(new ChatEntry(ChatEntryKind.Error, "The API key was rejected. Click the key icon above to change it."));
        }
        catch (AnthropicRateLimitException)
        {
            Show(new ChatEntry(ChatEntryKind.Error, "Rate limit reached. Wait a moment and try again."));
        }
        catch (AnthropicApiException ex)
        {
            Show(new ChatEntry(ChatEntryKind.Error, ex.Message.Contains("credit balance", StringComparison.OrdinalIgnoreCase)
                ? "Your Anthropic account has no API credits. Add some at console.anthropic.com (Plans & Billing)."
                : $"API error: {ex.Message}"));
        }
        catch (Exception ex) when (ex is AnthropicIOException or HttpRequestException)
        {
            Show(new ChatEntry(ChatEntryKind.Error, $"Can't reach the API: {ex.Message}"));
        }
        finally
        {
            _aiEntries.Remove(working);
            _aiCts.Dispose();
            _aiCts = null;
            AiSendButton.Content = "";
            AiSendButton.ToolTip = "Send (Enter)";
        }
    }

    private void SendAiInput()
    {
        if (_aiCts != null) return;
        AskAi(AiInput.Text);
        AiInput.Clear();
    }

    private void AiToggle_Click(object sender, RoutedEventArgs e) => ToggleAi();

    private void AiSend_Click(object sender, RoutedEventArgs e)
    {
        if (_aiCts != null) _aiCts.Cancel();
        else SendAiInput();
    }

    private void AiInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        e.Handled = true;
        SendAiInput();
    }

    private void AiSuggestion_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string request) AskAi(request);
    }

    private void AiExplain_Click(object sender, RoutedEventArgs e) =>
        AskAi(Code.SelectionLength > 0 ? "Explain what the selected code does." : "Explain what this script does.", forceFile: true);

    private void AiFix_Click(object sender, RoutedEventArgs e)
    {
        var output = LuaLog.Instance.Lines.TakeLast(40).Select(l => l.Text);
        AskAi("This script has an error (see Output). Find the cause and fix it in the file.",
              forceFile: true, extra: $"Recent Output:\n```\n{string.Join('\n', output)}\n```");
    }

    private void AiAction_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ChatEntry { FilePath: { } path } && File.Exists(path)) OpenFile(path);
    }

    private void AskAboutSelected_Click(object sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is not ScriptFile file || OpenFile(file.Path) == null) return;
        ShowAi(true);
        AskAi($"Explain what {file.Name} does and how to use it.", forceFile: true);
    }

    private void AiClear_Click(object sender, RoutedEventArgs e)
    {
        _aiCts?.Cancel();
        _ai.Reset();
        _aiEntries.Clear();
    }

    private void AiKey_Click(object sender, RoutedEventArgs e)
    {
        AiKeyPanel.Visibility = AiKeyPanel.Visibility == Visibility.Visible && _aiKey != null
            ? Visibility.Collapsed
            : Visibility.Visible;
        if (AiKeyPanel.Visibility == Visibility.Visible) AiKeyBox.Focus();
    }

    private void AiSaveKey_Click(object sender, RoutedEventArgs e)
    {
        string key = AiKeyBox.Password.Trim();
        if (key.Length == 0) return;
        if (!AiKeyStore.Save(key)) SetStatus("Couldn't save the key to disk; it works until the editor closes.");
        _aiKey = key;
        AiKeyBox.Clear();
        UpdateAiKeyPanel();
        AiInput.Focus();
    }

    private void AiRemoveKey_Click(object sender, RoutedEventArgs e)
    {
        AiKeyStore.Clear();
        _aiKey = null;
        UpdateAiKeyPanel();
    }

    private void AiMessage_SizeChanged(object sender, SizeChangedEventArgs e) => AiScroll.ScrollToBottom();

    // ---------------- State ----------------

    private void UpdateState()
    {
        var doc = Current;
        bool runs = doc?.Language?.Runs == true;
        bool loaded = runs && ScriptService.IsLoaded(doc!.ScriptName);

        SaveButton.IsEnabled = doc != null;
        RunButton.IsEnabled = runs;
        StopButton.IsEnabled = loaded;
        RunButton.ToolTip = !runs ? "This language can't run inside apathy" : loaded ? "Reload (F5)" : "Run (F5)";
        RunningText.Visibility = loaded ? Visibility.Visible : Visibility.Collapsed;

        LanguageText.Text = doc?.Language is { } language
            ? language.Extension == ".enma" ? $"Enma {Enma.EnmaCompiler.Version} → Lua 5.2"
            : language.Runs ? "Lua 5.2" : $"{language.Name} (editing only)"
            : "";
        TitleFile.Text = doc == null ? "" : $"scripts / {doc.Name}{(doc.IsDirty ? "  ●" : "")}";
    }

    private void UpdateCaret()
    {
        var caret = Code.TextArea.Caret;
        CaretText.Text = Current == null ? "" : $"Ln {caret.Line}, Col {caret.Column}";
    }

    private void SetStatus(string text) => StatusText.Text = text;

    // ---------------- Events ----------------

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var modifiers = Keyboard.Modifiers;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        bool handled = true;

        switch (key)
        {
            case Key.S when modifiers == (ModifierKeys.Control | ModifierKeys.Shift):
                SaveAll();
                break;
            case Key.S when modifiers == ModifierKeys.Control:
                if (Current is { } doc) Save(doc);
                break;
            case Key.N when modifiers == ModifierKeys.Control:
                NewName.Focus();
                NewName.SelectAll();
                break;
            case Key.W when modifiers == ModifierKeys.Control:
                if (Current is { } open) CloseTab(open);
                break;
            case Key.I when modifiers == ModifierKeys.Control:
                ToggleAi();
                break;
            case Key.E when modifiers == ModifierKeys.Control:
                ToggleStudio();
                break;
            case Key.B when modifiers == (ModifierKeys.Control | ModifierKeys.Shift):
                ShowStudio(TabLua);
                break;
            case Key.F8 when modifiers == ModifierKeys.None:
                NextProblem();
                break;
            case Key.F when modifiers == ModifierKeys.Control:
                OpenFind(replace: false);
                break;
            case Key.H when modifiers == ModifierKeys.Control:
                OpenFind(replace: true);
                break;
            case Key.F3 when FindBar.IsVisible:
                FindNext(backwards: modifiers == ModifierKeys.Shift);
                break;
            case Key.Space when modifiers == ModifierKeys.Control && Code.TextArea.IsKeyboardFocusWithin:
                _completion.Show();
                break;
            case Key.Escape when FindBar.IsVisible && !AiInput.IsKeyboardFocused && !_completion.IsOpen:
                CloseFind();
                break;
            case Key.F5 when modifiers == ModifierKeys.Shift:
                Stop();
                break;
            case Key.F5 when modifiers == ModifierKeys.None:
                Run();
                break;
            default:
                handled = false;
                break;
        }
        e.Handled = handled;
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        var dirty = _documents.Where(d => d.IsDirty).ToList();
        if (dirty.Count == 0) return;

        string names = string.Join(", ", dirty.Select(d => d.Name));
        var answer = ConfirmDialog.Ask(this, "Unsaved changes", $"Save changes to {names}?", "Save all", "Don't save", "Cancel");
        if (answer == null || (answer == true && !SaveAll())) e.Cancel = true;
    }

    private void TabList_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowDocument();

    private void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is EditorDocument doc) CloseTab(doc);
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        if (NewName.Text.Trim().Length > 0) CreateFile();
        else NewName.Focus();
    }

    private void NewName_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) CreateFile();
        else if (e.Key == Key.Escape && Current != null) Code.Focus();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (Current is { } doc) Save(doc);
    }

    private void SaveAll_Click(object sender, RoutedEventArgs e) => SaveAll();

    private void Run_Click(object sender, RoutedEventArgs e) => Run();

    private void Stop_Click(object sender, RoutedEventArgs e) => Stop();

    private void Find_Click(object sender, RoutedEventArgs e) => OpenFind(replace: false);

    private void FileList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FileList.SelectedItem is ScriptFile file) OpenFile(file.Path);
    }

    private void FileList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && FileList.SelectedItem is ScriptFile file) OpenFile(file.Path);
        else if (e.Key == Key.Delete) DeleteSelected();
    }

    private void OpenSelected_Click(object sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is ScriptFile file) OpenFile(file.Path);
    }

    private void Delete_Click(object sender, RoutedEventArgs e) => DeleteSelected();

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshFiles();

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(ScriptService.Folder);
        Process.Start(new ProcessStartInfo(ScriptService.Folder) { UseShellExecute = true });
    }

    private void ClearOutput_Click(object sender, RoutedEventArgs e) => LuaLog.Instance.Clear();

    private void Output_SizeChanged(object sender, SizeChangedEventArgs e) => OutputScroll.ScrollToBottom();

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
