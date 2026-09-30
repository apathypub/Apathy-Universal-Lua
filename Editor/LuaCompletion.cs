using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;

namespace Apathy.Editor;

/// <summary>
/// Code hints for the script editor: a completion list while typing (globals, keywords, snippets,
/// members after "." / ":", game.on event names, words from the file), the signature of a function
/// after "(", and a description when hovering a known name. The apathy API is only suggested when
/// <c>apiEnabled</c> says the file runs in apathy (.lua).
/// </summary>
public sealed partial class LuaCompletion
{
    private readonly TextEditor _editor;
    private readonly FrameworkElement _resources;
    private readonly Func<bool> _apiEnabled;
    private readonly ToolTip _hover = new();
    private CompletionWindow? _window;
    private InsightWindow? _insight;

    private static readonly List<LuaSymbol> AllMethods = LuaApi.Modules["string"]
        .Concat(LuaApi.Types.Where(t => !t.Key.StartsWith("event:")).SelectMany(t => t.Value))
        .Where(s => s.Kind is LuaSymbolKind.Function or LuaSymbolKind.Method)
        .DistinctBy(s => s.Name)
        .ToList();

    private static readonly HashSet<string> GlobalNames = LuaApi.Globals.Select(s => s.Name).ToHashSet();

    public LuaCompletion(TextEditor editor, FrameworkElement resources, Func<bool> apiEnabled)
    {
        _editor = editor;
        _resources = resources;
        _apiEnabled = apiEnabled;

        editor.TextArea.TextEntering += TextEntering;
        editor.TextArea.TextEntered += TextEntered;
        editor.MouseHover += MouseHover;
        editor.MouseHoverStopped += (_, _) => _hover.IsOpen = false;
        editor.DocumentChanged += (_, _) => Close();
    }

    /// <summary>True while the completion list or signature hint is showing (Escape belongs to it).</summary>
    public bool IsOpen => _window != null || _insight != null;

    /// <summary>Opens the list on request (Ctrl+Space), even mid-word or with nothing typed.</summary>
    public void Show() => Open(explicitRequest: true);

    public void Close()
    {
        _window?.Close();
        _insight?.Close();
    }

    // ---------------- Typing ----------------

    private void TextEntering(object? sender, TextCompositionEventArgs e)
    {
        // Any non-word character ends the word being completed; only Enter / Tab insert a suggestion.
        if (_window != null && e.Text.Length > 0 && !IsIdent(e.Text[0])) _window.Close();
    }

    private void TextEntered(object? sender, TextCompositionEventArgs e)
    {
        if (e.Text.Length == 0) return;
        if (_window != null)
        {
            // Nothing matches what's typed: get out of the way so Enter makes a new line.
            if (_window.CompletionList.ListBox.Items.Count == 0) _window.Close();
            return;
        }

        char c = e.Text[^1];
        int caret = _editor.CaretOffset;
        if (c is '.' or ':' or '"' or '\'')
            Open(explicitRequest: false);
        else if (IsIdent(c) && !char.IsDigit(c) && (caret < 2 || !IsIdent(_editor.Document.GetCharAt(caret - 2))))
            Open(explicitRequest: false); // first letter of a new word
        else if (c == '(')
            ShowSignature(caret - 1);
        else if (c == ')')
            _insight?.Close();
    }

    // ---------------- Completion list ----------------

    private void Open(bool explicitRequest)
    {
        _window?.Close();
        var document = _editor.Document;
        int caret = _editor.CaretOffset;
        var symbols = Candidates(caret, explicitRequest, out int wordStart);
        if (symbols == null || symbols.Count == 0) return;

        string prefix = document.GetText(wordStart, caret - wordStart);
        if (!explicitRequest && prefix.Length > 0 &&
            !symbols.Any(s => s.Name.Contains(prefix, StringComparison.OrdinalIgnoreCase)))
            return;

        var window = new CompletionWindow(_editor.TextArea)
        {
            StartOffset = wordStart,
            Width = 340,
            Background = Brush("Ed.Panel"),
            BorderBrush = Brush("Ed.Line"),
            BorderThickness = new Thickness(1),
            CloseWhenCaretAtBeginning = !explicitRequest && prefix.Length > 0,
        };
        var list = window.CompletionList;
        list.ListBox.Background = Brush("Ed.Panel");
        list.ListBox.Foreground = Brush("Sk.Text");
        list.ListBox.BorderThickness = new Thickness(0);
        list.ListBox.ItemContainerStyle = (Style)_resources.FindResource("CompletionRow");

        foreach (var symbol in symbols
                     .OrderBy(s => s.Kind == LuaSymbolKind.Variable) // API before words from the file
                     .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
            list.CompletionData.Add(new CompletionItem(symbol, this));

        window.Closed += (_, _) =>
        {
            if (_window == window) _window = null;
        };
        _window = window;
        window.Show();
        if (prefix.Length > 0) list.SelectItem(prefix);
    }

    private enum Where { Code, Comment, String }

    /// <summary>What to suggest at the caret, or null for nothing. <paramref name="wordStart"/> is where the typed word begins.</summary>
    private List<LuaSymbol>? Candidates(int caret, bool explicitRequest, out int wordStart)
    {
        var document = _editor.Document;
        var line = document.GetLineByOffset(caret);
        string before = document.GetText(line.Offset, caret - line.Offset);
        wordStart = caret;
        while (wordStart > line.Offset && IsIdent(document.GetCharAt(wordStart - 1))) wordStart--;
        string prefix = document.GetText(wordStart, caret - wordStart);
        bool api = _apiEnabled();

        switch (Classify(before))
        {
            case Where.Comment:
                return null;
            case Where.String:
                return api && EventString().IsMatch(before) ? LuaApi.Events : null;
        }
        if (prefix.Length > 0 && char.IsDigit(prefix[0])) return null; // a number like 0x1F

        char separator = wordStart > line.Offset ? document.GetCharAt(wordStart - 1) : '\0';
        if (separator is '.' or ':')
        {
            int objectEnd = wordStart - 1;
            if (objectEnd > line.Offset && document.GetCharAt(objectEnd - 1) == '.') return null; // ".." concatenation
            int objectStart = objectEnd;
            while (objectStart > line.Offset && IsIdent(document.GetCharAt(objectStart - 1))) objectStart--;
            if (!api || objectStart == objectEnd) return null;
            return Members(document.GetText(objectStart, objectEnd - objectStart), separator,
                           document.GetText(0, objectStart));
        }

        // Typing the name of something new ("local speed", "function update"): nothing to suggest.
        if (!explicitRequest && NewName().IsMatch(before[..^prefix.Length])) return null;

        var symbols = api
            ? new List<LuaSymbol>(LuaApi.Globals)
            : LuaApi.Globals.Where(s => s.Kind == LuaSymbolKind.Keyword).ToList();
        symbols.AddRange(DocumentWords(prefix));
        return symbols;
    }

    private static List<LuaSymbol>? Members(string owner, char separator, string textBefore)
    {
        if (separator == '.' && LuaApi.Modules.TryGetValue(owner, out var members)) return members;
        if (InferType(owner, textBefore) is { } type && LuaApi.Types.TryGetValue(type, out var typed))
        {
            // h:get() calls a method, h.value reads a field.
            var fitting = typed.Where(s => (s.Kind == LuaSymbolKind.Method) == (separator == ':')).ToList();
            return fitting.Count > 0 ? fitting : null;
        }
        return separator == ':' ? AllMethods : null; // unknown value: string methods and control handles
    }

    /// <summary>
    /// Guesses what a variable holds from how it was assigned: "local h = ui.checkbox(...)" -> Checkbox,
    /// or the parameter of "game.on("kill", function(e)" -> the kill event's data.
    /// </summary>
    private static string? InferType(string name, string textBefore)
    {
        string n = Regex.Escape(name);
        var calls = Regex.Matches(textBefore, $@"\b{n}\s*=\s*([A-Za-z_]\w*\s*\.\s*[A-Za-z_]\w*)\s*\(");
        if (calls.Count > 0 &&
            LuaApi.Returns.TryGetValue(Regex.Replace(calls[^1].Groups[1].Value, @"\s", ""), out var type))
            return type;

        var events = Regex.Matches(textBefore, $@"game\s*\.\s*on\s*\(\s*[""'](\w+)[""']\s*,\s*function\s*\(\s*{n}\s*\)");
        return events.Count > 0 ? "event:" + events[^1].Groups[1].Value : null;
    }

    /// <summary>Names used elsewhere in the file (locals, functions, fields) that aren't API names.</summary>
    private IEnumerable<LuaSymbol> DocumentWords(string prefix)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { prefix };
        foreach (Match match in Word().Matches(_editor.Document.Text))
        {
            string word = match.Value;
            if (GlobalNames.Contains(word) || !seen.Add(word)) continue;
            yield return new LuaSymbol(word, LuaSymbolKind.Variable, word, "Used in this file.");
            if (seen.Count > 400) yield break;
        }
    }

    /// <summary>Where the end of a line (the text before the caret) is: code, a -- comment or a string.</summary>
    private static Where Classify(string line)
    {
        char quote = '\0';
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quote != '\0')
            {
                if (c == '\\') i++;
                else if (c == quote) quote = '\0';
            }
            else if (c is '"' or '\'') quote = c;
            else if (c == '-' && i + 1 < line.Length && line[i + 1] == '-') return Where.Comment;
        }
        return quote != '\0' ? Where.String : Where.Code;
    }

    private static bool IsIdent(char c) => char.IsLetterOrDigit(c) || c == '_';

    [GeneratedRegex(@"game\s*\.\s*on\s*\(\s*[""']\w*$")]
    private static partial Regex EventString();

    [GeneratedRegex(@"(\b(local|function|for)\s+|\blocal\s+[\w\s,]*,\s*|\bfunction\s*\([\w\s,]*)$")]
    private static partial Regex NewName();

    // Identifiers of 3+ characters that aren't a member access (x.name / x:name).
    [GeneratedRegex(@"(?<![.:\w])[A-Za-z_]\w{2,}")]
    private static partial Regex Word();

    // ---------------- Signature and hover ----------------

    /// <summary>The known function / field whose name ends right before <paramref name="end"/>.</summary>
    private LuaSymbol? SymbolEndingAt(int end)
    {
        var document = _editor.Document;
        int start = end;
        while (start > 0 && IsIdent(document.GetCharAt(start - 1))) start--;
        if (start == end || !_apiEnabled()) return null;
        string name = document.GetText(start, end - start);

        char separator = start > 0 ? document.GetCharAt(start - 1) : '\0';
        if (separator is not ('.' or ':'))
            return LuaApi.Find(name) is { Kind: not LuaSymbolKind.Keyword } global ? global : null;

        int ownerEnd = start - 1, ownerStart = ownerEnd;
        while (ownerStart > 0 && IsIdent(document.GetCharAt(ownerStart - 1))) ownerStart--;
        if (ownerStart == ownerEnd) return null;
        string owner = document.GetText(ownerStart, ownerEnd - ownerStart);
        if (separator == '.' && LuaApi.Find($"{owner}.{name}") is { } member) return member;
        return Members(owner, separator, document.GetText(0, ownerStart))?.FirstOrDefault(s => s.Name == name);
    }

    private void ShowSignature(int parenOffset)
    {
        _insight?.Close();
        if (SymbolEndingAt(parenOffset) is not { Kind: LuaSymbolKind.Function or LuaSymbolKind.Method } symbol) return;

        var insight = new InsightWindow(_editor.TextArea)
        {
            Content = Describe(symbol),
            Background = Brush("Ed.Panel"),
            BorderBrush = Brush("Ed.Line"),
            BorderThickness = new Thickness(1),
            StartOffset = parenOffset + 1,
        };
        insight.Closed += (_, _) =>
        {
            if (_insight == insight) _insight = null;
        };
        _insight = insight;
        insight.Show();
    }

    private void MouseHover(object sender, MouseEventArgs e)
    {
        var position = _editor.GetPositionFromPoint(e.GetPosition(_editor));
        if (position == null || _editor.Document == null) return;
        var document = _editor.Document;
        int offset = document.GetOffset(position.Value.Location);
        if (offset >= document.TextLength || !IsIdent(document.GetCharAt(offset))) return;

        var line = document.GetLineByOffset(offset);
        if (Classify(document.GetText(line.Offset, offset - line.Offset)) != Where.Code) return;

        int end = offset;
        while (end < document.TextLength && IsIdent(document.GetCharAt(end))) end++;
        if (SymbolEndingAt(end) is not { } symbol) return;

        _hover.PlacementTarget = _editor;
        _hover.Placement = System.Windows.Controls.Primitives.PlacementMode.Mouse;
        _hover.Content = Describe(symbol);
        _hover.IsOpen = true;
        e.Handled = true;
    }

    // ---------------- Visuals ----------------

    private Brush Brush(string key) => (Brush)_resources.FindResource(key);

    private static readonly FontFamily CodeFont = new("Cascadia Mono, Consolas");

    /// <summary>Signature over description, used for the list's tooltip, the signature hint and hover.</summary>
    private FrameworkElement Describe(LuaSymbol symbol)
    {
        var panel = new StackPanel { MaxWidth = 420, Margin = new Thickness(2) };
        panel.Children.Add(new TextBlock
        {
            Text = symbol.Signature,
            FontFamily = CodeFont,
            Foreground = KindBrush(symbol.Kind),
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock
        {
            Text = symbol.Doc,
            Margin = new Thickness(0, 4, 0, 0),
            Foreground = Brush("Sk.TextMuted"),
            TextWrapping = TextWrapping.Wrap,
        });
        return panel;
    }

    private static readonly Dictionary<LuaSymbolKind, (string Glyph, Brush Color)> Kinds = new()
    {
        [LuaSymbolKind.Keyword] = ("k", Frozen("#C586C0")),
        [LuaSymbolKind.Function] = ("ƒ", Frozen("#DCDCAA")),
        [LuaSymbolKind.Method] = ("ƒ", Frozen("#DCDCAA")),
        [LuaSymbolKind.Module] = ("{}", Frozen("#4EC9B0")),
        [LuaSymbolKind.Field] = ("•", Frozen("#9CDCFE")),
        [LuaSymbolKind.Constant] = ("#", Frozen("#569CD6")),
        [LuaSymbolKind.Event] = ("ev", Frozen("#CE9178")),
        [LuaSymbolKind.Variable] = ("ab", Frozen("#8A8A8A")),
        [LuaSymbolKind.Snippet] = ("⋯", Frozen("#B5CEA8")),
    };

    private static Brush KindBrush(LuaSymbolKind kind) => Kinds[kind].Color;

    private static SolidColorBrush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    private sealed class CompletionItem(LuaSymbol symbol, LuaCompletion owner) : ICompletionData
    {
        private object? _content, _description;

        public ImageSource? Image => null;

        public string Text => symbol.Name;

        // API and keywords win over words from the file when both match equally well.
        public double Priority => symbol.Kind == LuaSymbolKind.Variable ? 0 : 1;

        public object Content => _content ??= BuildContent();

        public object Description => _description ??= owner.Describe(symbol);

        private StackPanel BuildContent()
        {
            var (glyph, color) = Kinds[symbol.Kind];
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock { Text = glyph, Width = 20, Foreground = color, FontFamily = CodeFont });
            row.Children.Add(new TextBlock { Text = symbol.Name, FontFamily = CodeFont, Foreground = owner.Brush("Sk.Text") });
            if (symbol.Kind is LuaSymbolKind.Snippet or LuaSymbolKind.Event)
            {
                row.Children.Add(new TextBlock
                {
                    Text = "  " + symbol.Signature, Foreground = owner.Brush("Sk.TextMuted"),
                    TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 220,
                });
            }
            return row;
        }

        public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
        {
            string text = symbol.InsertText;
            if (symbol.Kind != LuaSymbolKind.Snippet)
            {
                textArea.Document.Replace(completionSegment, text);
                return;
            }

            // Snippets: keep the current indentation on every line and put the caret at $0.
            var line = textArea.Document.GetLineByOffset(completionSegment.Offset);
            string indent = TextUtilities.GetLeadingWhitespace(textArea.Document, line) is { } ws
                ? textArea.Document.GetText(ws)
                : "";
            text = text.Replace("\n", "\n" + indent);
            int start = completionSegment.Offset;
            int caret = text.IndexOf("$0", StringComparison.Ordinal);
            text = text.Replace("$0", "");
            textArea.Document.Replace(completionSegment, text);
            if (caret >= 0) textArea.Caret.Offset = start + caret;
        }
    }
}
