using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Markup;
using System.Xml;
using System.Xml.Linq;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using ICSharpCode.AvalonEdit.Rendering;
using ICSharpCode.AvalonEdit.Search;
using Microsoft.Win32;
using Newtonsoft.Json.Linq;
using XmlJsonFormatter.Models;
using XmlJsonFormatter.Services;

namespace XmlJsonFormatter;

public partial class MainWindow : Window
{
    public static readonly RoutedCommand CloseTabCommand = new RoutedCommand();

    // ──────────────────────────── line highlight renderer ────────────────────────────
    private sealed class LineHighlightRenderer : IBackgroundRenderer
    {
        private readonly TextEditor _editor;
        private static readonly Brush HighlightBrush = MakeBrush();
        private static Brush MakeBrush()
        {
            var b = new SolidColorBrush(Color.FromArgb(80, 0x00, 0x7A, 0xCC));
            b.Freeze();
            return b;
        }

        public int? Line { get; set; }
        public KnownLayer Layer => KnownLayer.Background;

        public LineHighlightRenderer(TextEditor editor) => _editor = editor;

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (Line is not int line) return;
            if (line < 1 || line > _editor.Document.LineCount) return;

            var docLine = _editor.Document.GetLineByNumber(line);
            foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, docLine))
            {
                drawingContext.DrawRectangle(
                    HighlightBrush, null,
                    new Rect(0, rect.Top, textView.ActualWidth, rect.Height));
            }
        }
    }


    private sealed class EditorTab
    {
        public TabItem TabItem { get; }
        public TextEditor Editor { get; }
        public LineHighlightRenderer HighlightRenderer { get; }
        public SearchPanel SearchPanel { get; set; } = null!;
        public TreeView? InlineTreeView { get; set; }
        public Button? TextViewButton { get; set; }
        public Button? GridViewButton { get; set; }
        public bool IsGridMode { get; set; }
        public string? FilePath { get; set; }
        public FileType FileType { get; set; } = FileType.None;
        public int RightClickLine { get; set; }
        public int RightClickColumn { get; set; }
        public bool IsModified { get; set; }

        public EditorTab(TabItem ti, TextEditor ed)
        {
            TabItem = ti;
            Editor = ed;
            HighlightRenderer = new LineHighlightRenderer(ed);
            ed.TextArea.TextView.BackgroundRenderers.Add(HighlightRenderer);
        }

        public string TabTitle => FilePath is null ? "untitled"
                                                   : System.IO.Path.GetFileName(FilePath);
    }

    private enum FileType { None, Xml, Json }

    // ──────────────────────────── instance state ────────────────────────────
    private static IHighlightingDefinition? _xmlHighlighting;
    private static IHighlightingDefinition? _jsonHighlighting;
    // Tab drag-drop state
    private Point _tabDragStart;
    private bool _tabDragging;

    // ──────────────────────────── init ────────────────────────────
    public MainWindow() => InitializeComponent();

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // Start with one blank tab
        AddEditorTab(null, string.Empty);

        // File drag-drop: Explorer → Window
        PreviewDrop += Window_FileDrop;
        PreviewDragOver += Window_DragOver;
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_FileDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        foreach (var file in files.Where(
            f => Path.GetExtension(f).ToLowerInvariant() is ".xml" or ".json" or ".xsd" or ".xsl" or ".xslt"))
        {
            OpenFileFromPath(file);
        }
    }

    // ──────────────────────────── tab management ────────────────────────────
    private EditorTab AddEditorTab(string? filePath, string content)
    {
        var editor = CreateEditor();
        editor.Text = content;

        var titleBlock = new TextBlock
        {
            Text = filePath is null ? "untitled" : Path.GetFileName(filePath),
            Foreground = System.Windows.Media.Brushes.LightGray,
            VerticalAlignment = VerticalAlignment.Center
        };

        var closeBtn = new Button
        {
            Content = "✕",
            FontSize = 10,
            Width = 16,
            Height = 16,
            Padding = new Thickness(0),
            Margin = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = System.Windows.Media.Brushes.Gray,
            Cursor = System.Windows.Input.Cursors.Arrow,
            ToolTip = "Close tab"
        };

        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        header.Children.Add(titleBlock);
        header.Children.Add(closeBtn);

        var tabItem = new TabItem
        {
            Header = header,
            AllowDrop = true
        };

        var state = new EditorTab(tabItem, editor) { FilePath = filePath };
        state.SearchPanel = SearchPanel.Install(editor.TextArea);
        state.SearchPanel.MarkerBrush = new SolidColorBrush(Color.FromArgb(160, 0xFF, 0xA5, 0x00));
        tabItem.Content = BuildTabViewContainer(state);
        tabItem.Tag = state;

        // Track modifications
        editor.Document.Changed += (s, e) => { state.IsModified = true; RefreshTabTitle(state); };

        // Close button handler
        closeBtn.Click += (s, e) =>
        {
            e.Handled = true; // don't trigger tab selection
            CloseEditorTab(state);
        };

        // Hover style for close button
        closeBtn.MouseEnter += (s, e) =>
            closeBtn.Foreground = System.Windows.Media.Brushes.White;
        closeBtn.MouseLeave += (s, e) =>
            closeBtn.Foreground = System.Windows.Media.Brushes.Gray;

        // ─── Tab drag-drop reordering ───
        header.PreviewMouseLeftButtonDown += (s, e) =>
        {
            _tabDragStart = e.GetPosition(null);
        };

        header.PreviewMouseMove += (s, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed || _tabDragging) return;
            var pos = e.GetPosition(null);
            if (Math.Abs(pos.X - _tabDragStart.X) > SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(pos.Y - _tabDragStart.Y) > SystemParameters.MinimumVerticalDragDistance)
            {
                _tabDragging = true;
                DragDrop.DoDragDrop(tabItem, tabItem, DragDropEffects.Move);
                _tabDragging = false;
            }
        };

        tabItem.DragOver += (s, e) =>
        {
            e.Effects = e.Data.GetDataPresent(typeof(TabItem))
                ? DragDropEffects.Move
                : DragDropEffects.None;
            e.Handled = true;
        };

        tabItem.Drop += (s, e) =>
        {
            if (e.Data.GetData(typeof(TabItem)) is not TabItem sourceTab || sourceTab == tabItem) return;
            int srcIdx = editorTabs.Items.IndexOf(sourceTab);
            int dstIdx = editorTabs.Items.IndexOf(tabItem);
            if (srcIdx < 0 || dstIdx < 0) return;
            editorTabs.Items.Remove(sourceTab);
            editorTabs.Items.Insert(dstIdx, sourceTab);
            editorTabs.SelectedItem = sourceTab;
            e.Handled = true;
        };

        editorTabs.Items.Add(tabItem);
        editorTabs.SelectedItem = tabItem;

        if (filePath is not null)
        {
            var ft = DetectFileType(filePath, content);
            ApplyFileTypeToTab(state, ft);
        }

        // Mark clean after initial load
        state.IsModified = false;

        BuildEditorContextMenu(state);
        return state;
    }

    private TextEditor CreateEditor()
    {
        var editor = new TextEditor
        {
            FontFamily = new System.Windows.Media.FontFamily("Consolas, Courier New, Monospace"),
            FontSize = 13,
            ShowLineNumbers = true,
            WordWrap = false,
            Background = System.Windows.Media.Brushes.Transparent,
            Foreground = new System.Windows.Media.SolidColorBrush(
                                  System.Windows.Media.Color.FromRgb(0xD4, 0xD4, 0xD4)),
            LineNumbersForeground = new System.Windows.Media.SolidColorBrush(
                                        System.Windows.Media.Color.FromRgb(0x85, 0x85, 0x85))
        };
        editor.Options.EnableHyperlinks = false;
        editor.Options.EnableEmailHyperlinks = false;
        editor.Options.ConvertTabsToSpaces = true;
        editor.Options.IndentationSize = 4;
        return editor;
    }

    private EditorTab? ActiveTab =>
        editorTabs.SelectedItem is TabItem ti ? ti.Tag as EditorTab : null;

    private void EditorTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var tab = ActiveTab;
        if (tab is null) return;

        UpdateStatusBar(tab);
        UpdateRightPanelForTab(tab);
    }

    private void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        var tab = ActiveTab;
        if (tab is null) return;
        CloseEditorTab(tab);
    }

    private void CloseEditorTab(EditorTab tab)
    {
        if (editorTabs.Items.Count <= 1)
        {
            // Keep at least one tab — just clear it instead of closing
            tab.FilePath = null;
            tab.IsModified = false;
            tab.Editor.Text = string.Empty;
            ApplyFileTypeToTab(tab, FileType.None);
            RefreshTabTitle(tab);
            return;
        }

        if (tab.IsModified)
        {
            var name = tab.FilePath is null ? "untitled" : Path.GetFileName(tab.FilePath);
            var result = MessageBox.Show(
                $"'{name}' has unsaved changes.\nDo you want to save before closing?",
                "Unsaved Changes",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Cancel) return;
            if (result == MessageBoxResult.Yes)
            {
                SaveFile();
                // User may have cancelled the Save As dialog for untitled files
                if (tab.IsModified) return;
            }
        }

        editorTabs.Items.Remove(tab.TabItem);
    }

    private void RefreshTabTitle(EditorTab tab)
    {
        if (tab.TabItem.Header is not StackPanel sp) return;
        if (sp.Children[0] is TextBlock tb)
            tb.Text = tab.IsModified ? tab.TabTitle + " ●" : tab.TabTitle;
    }

    private void UpdateTabHeader(EditorTab tab)
    {
        tab.IsModified = false;
        RefreshTabTitle(tab);
    }

    private void UpdateStatusBar(EditorTab tab)
    {
        filePathLabel.Text = tab.FilePath ?? string.Empty;
        fileTypeLabel.Text = tab.FileType switch
        {
            FileType.Xml => tab.FilePath is null ? "XML"
                : Path.GetExtension(tab.FilePath).ToUpperInvariant().TrimStart('.'),
            FileType.Json => "JSON",
            _ => string.Empty
        };
    }

    private void UpdateRightPanelForTab(EditorTab tab)
    {
        // XSD / XSL / XSLT are loaded as FileType.Xml for syntax highlighting,
        // but XPath querying only makes sense for plain .xml files.
        var ext = tab.FilePath is null ? string.Empty
                  : Path.GetExtension(tab.FilePath).ToLowerInvariant();
        bool isXml = tab.FileType == FileType.Xml
                      && ext is not (".xsd" or ".xsl" or ".xslt");
        bool isJson = tab.FileType == FileType.Json;
        bool active = isXml || isJson;

        // Block the content area entirely when no XML/JSON file is open
        xpathContent.IsEnabled = active;
        xpathContent.Opacity = active ? 1.0 : 0.3;

        // Rename the XPath tab according to what's open
        xpathTab.Header = isJson ? "JSONPath" : "XPath";

        // Update the label inside the XPath tab
        xpathLabel.Text = isJson
            ? "JSONPath Expression  (Ctrl+Enter to execute):"
            : "XPath Expression  (Ctrl+Enter to execute):";
        executeXpathBtn.Content = isJson
            ? "▶  Execute JSONPath"
            : "▶  Execute XPath";

        // Enable/disable the inline Grid button
        if (tab.GridViewButton is not null)
        {
            bool inlineGridOk = tab.FileType == FileType.Xml || tab.FileType == FileType.Json;
            tab.GridViewButton.IsEnabled = inlineGridOk;
            tab.GridViewButton.Opacity = inlineGridOk ? 1.0 : 0.4;
            if (!inlineGridOk && tab.IsGridMode)
                SwitchToTextView(tab);
        }
    }

    // ──────────────────────────── keyboard shortcuts ────────────────────────────
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.N && e.KeyboardDevice.Modifiers == ModifierKeys.Control)
        { NewFile(); e.Handled = true; }
        else if (e.Key == Key.O && e.KeyboardDevice.Modifiers == ModifierKeys.Control)
        { OpenFile(); e.Handled = true; }
        else if (e.Key == Key.S && e.KeyboardDevice.Modifiers == ModifierKeys.Control)
        { SaveFile(); e.Handled = true; }
        else if (e.Key == Key.F && e.KeyboardDevice.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        { FormatDocument(); e.Handled = true; }
        else if (e.Key == Key.F && e.KeyboardDevice.Modifiers == ModifierKeys.Control)
        { OpenFind(); e.Handled = true; }
    }

    // ──────────────────────────── File menu ────────────────────────────
    private void Find_Click(object sender, RoutedEventArgs e) => OpenFind();
    private void NewFile_Click(object sender, RoutedEventArgs e) => NewFile();
    private void OpenFile_Click(object sender, RoutedEventArgs e) => OpenFile();
    private void SaveFile_Click(object sender, RoutedEventArgs e) => SaveFile();
    private void SaveFileAs_Click(object sender, RoutedEventArgs e) => SaveFileAs();
    private void Exit_Click(object sender, RoutedEventArgs e) => Close();
    private void FormatDocument_Click(object sender, RoutedEventArgs e) => FormatDocument();
    private void ToggleWordWrap_Click(object sender, RoutedEventArgs e) => ToggleWordWrap();

    private void OpenFind()
    {
        var tab = ActiveTab;
        if (tab is null) return;
        tab.Editor.Focus();
        tab.SearchPanel.Open();
    }

    private void NewFile() => AddEditorTab(null, string.Empty);

    private void ToggleWordWrap()
    {
        var tab = ActiveTab;
        if (tab is null) return;
        bool wrap = !tab.Editor.WordWrap;
        // Apply to all open tabs
        foreach (var ti in editorTabs.Items.OfType<TabItem>())
            if (ti.Tag is EditorTab t) t.Editor.WordWrap = wrap;
        wordWrapMenuItem.IsChecked = wrap;
        wordWrapButton.Background = wrap
            ? new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x00, 0x7A, 0xCC))
            : System.Windows.Media.Brushes.Transparent;
    }

    private void OpenFile()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Open File",
            Filter = "XML / JSON / Schema Files|*.xml;*.json;*.xsd;*.xsl;*.xslt|XML Files|*.xml|JSON Files|*.json|XSD Schema Files|*.xsd|XSL / XSLT Files|*.xsl;*.xslt|All Files|*.*"
        };
        if (dlg.ShowDialog() == true)
            OpenFileFromPath(dlg.FileName);
    }

    private void OpenFileFromPath(string filePath)
    {
        // If the file is already open, just switch to that tab
        var existing = editorTabs.Items.OfType<TabItem>()
            .Select(ti => ti.Tag as EditorTab)
            .FirstOrDefault(t => t?.FilePath is not null &&
                string.Equals(t.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            editorTabs.SelectedItem = existing.TabItem;
            return;
        }

        try
        {
            var content = File.ReadAllText(filePath);
            var tab = ActiveTab;
            bool reuse = tab is not null && tab.FilePath is null
                          && string.IsNullOrWhiteSpace(tab.Editor.Text);
            if (reuse && tab is not null)
            {
                tab.FilePath = filePath;
                tab.Editor.Text = content;
                tab.IsModified = false;
                ApplyFileTypeToTab(tab, DetectFileType(filePath, content));
                UpdateTabHeader(tab);
                UpdateStatusBar(tab);
            }
            else
            {
                AddEditorTab(filePath, content);
            }
            ClearXPathResults();
            SetStatus($"Opened: {Path.GetFileName(filePath)}");
        }
        catch (Exception ex) { ShowError($"Failed to open file:\n{ex.Message}"); }
    }

    private void SaveFile()
    {
        var tab = ActiveTab;
        if (tab is null) return;
        if (tab.FilePath is null) { SaveFileAs(); return; }

        try
        {
            File.WriteAllText(tab.FilePath, tab.Editor.Text);
            tab.IsModified = false;
            RefreshTabTitle(tab);
            SetStatus($"Saved: {Path.GetFileName(tab.FilePath)}");
        }
        catch (Exception ex) { ShowError($"Failed to save file:\n{ex.Message}"); }
    }

    private void SaveFileAs()
    {
        var tab = ActiveTab;
        if (tab is null) return;

        var dlg = new SaveFileDialog
        {
            Title = "Save File As",
            Filter = tab.FileType == FileType.Json
                       ? "JSON Files|*.json|All Files|*.*"
                       : tab.FilePath is not null && Path.GetExtension(tab.FilePath).ToLowerInvariant() == ".xsd"
                           ? "XSD Schema Files|*.xsd|XML Files|*.xml|All Files|*.*"
                           : tab.FilePath is not null && Path.GetExtension(tab.FilePath).ToLowerInvariant() is ".xsl" or ".xslt"
                               ? "XSLT Files|*.xsl;*.xslt|XML Files|*.xml|All Files|*.*"
                               : "XML Files|*.xml|All Files|*.*",
            FileName = tab.FilePath is null ? string.Empty : Path.GetFileName(tab.FilePath)
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            File.WriteAllText(dlg.FileName, tab.Editor.Text);
            tab.FilePath = dlg.FileName;
            tab.IsModified = false;
            ApplyFileTypeToTab(tab, DetectFileType(dlg.FileName, tab.Editor.Text));
            UpdateTabHeader(tab);
            UpdateStatusBar(tab);
            SetStatus($"Saved: {Path.GetFileName(dlg.FileName)}");
        }
        catch (Exception ex) { ShowError($"Failed to save file:\n{ex.Message}"); }
    }

    // ──────────────────────────── Format / Auto-Indent ────────────────────────────
    private void FormatDocument()
    {
        var tab = ActiveTab;
        if (tab is null || string.IsNullOrWhiteSpace(tab.Editor.Text))
        { SetStatus("Nothing to format."); return; }

        try
        {
            if (tab.FileType == FileType.Xml)
            {
                ReplaceEditorText(tab, XmlService.FormatXml(tab.Editor.Text));
                SetStatus("XML auto-indented.");
            }
            else if (tab.FileType == FileType.Json)
            {
                ReplaceEditorText(tab, JsonService.FormatJson(tab.Editor.Text));
                SetStatus("JSON auto-indented.");
            }
            else
            {
                var text = tab.Editor.Text.TrimStart();
                if (text.StartsWith('<'))
                {
                    ReplaceEditorText(tab, XmlService.FormatXml(tab.Editor.Text));
                    ApplyFileTypeToTab(tab, FileType.Xml);
                    SetStatus("XML auto-indented.");
                }
                else if (text.StartsWith('{') || text.StartsWith('['))
                {
                    ReplaceEditorText(tab, JsonService.FormatJson(tab.Editor.Text));
                    ApplyFileTypeToTab(tab, FileType.Json);
                    SetStatus("JSON auto-indented.");
                }
                else
                {
                    SetStatus("Could not determine file type. Open an XML or JSON file first.");
                }
            }
        }
        catch (Exception ex) { SetStatus($"Format error: {ex.Message}"); }
    }

    private static void ReplaceEditorText(EditorTab tab, string newText)
    {
        tab.Editor.Document.BeginUpdate();
        try { tab.Editor.Document.Text = newText; }
        finally { tab.Editor.Document.EndUpdate(); }
    }

    // ──────────────────────────── Context menu per editor ────────────────────────────
    private void BuildEditorContextMenu(EditorTab tab)
    {
        var findItem = new MenuItem { Header = "Find…", InputGestureText = "Ctrl+F" };
        findItem.Click += (s, e) => OpenFind();

        var copyPathItem = new MenuItem { Header = "Copy XPath / JSON Path" };
        copyPathItem.Click += (s, e) => CopyPath(tab);

        var menu = new ContextMenu();
        menu.Items.Add(findItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(copyPathItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Cut", Command = ApplicationCommands.Cut });
        menu.Items.Add(new MenuItem { Header = "Copy", Command = ApplicationCommands.Copy });
        menu.Items.Add(new MenuItem { Header = "Paste", Command = ApplicationCommands.Paste });
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Select All", Command = ApplicationCommands.SelectAll });

        menu.Opened += (s, e) =>
        {
            bool isXml = tab.FileType == FileType.Xml;
            bool isJson = tab.FileType == FileType.Json;
            copyPathItem.Header = isJson ? "Copy JSON Path" : "Copy XPath";
            copyPathItem.IsEnabled = (isXml || isJson) && !string.IsNullOrWhiteSpace(tab.Editor.Text);
        };

        tab.Editor.PreviewMouseRightButtonDown += (s, e) =>
        {
            var textView = tab.Editor.TextArea.TextView;
            var mouseInView = e.GetPosition(textView);
            var tvPos = textView.GetPosition(mouseInView);
            if (tvPos.HasValue)
            {
                tab.RightClickLine = tvPos.Value.Line;
                tab.RightClickColumn = tvPos.Value.Column;
            }
            else
            {
                // GetPosition returns null for gutter/empty-space clicks;
                // fall back to finding the nearest visual line by Y coordinate.
                var docY = mouseInView.Y + textView.ScrollOffset.Y;
                var vLine = textView.VisualLines
                    .FirstOrDefault(vl => vl.VisualTop <= docY && docY < vl.VisualTop + vl.Height);
                tab.RightClickLine = vLine?.FirstDocumentLine.LineNumber
                                     ?? tab.Editor.TextArea.Caret.Line;
                tab.RightClickColumn = 0;
            }
        };

        tab.Editor.ContextMenu = menu;
        tab.Editor.TextArea.ContextMenu = menu;
    }

    private void CopyPath(EditorTab tab)
    {
        if (string.IsNullOrWhiteSpace(tab.Editor.Text)) return;

        try
        {
            string? path = tab.FileType == FileType.Json
                ? JsonService.GetJsonPathAtLine(tab.Editor.Text, tab.RightClickLine)
                : XmlService.GetXPathAtLine(tab.Editor.Text, tab.RightClickLine, tab.RightClickColumn);

            if (path is null)
            { SetStatus("Could not determine path at cursor position."); return; }

            // SetDataObject is more robust than SetText inside WPF
            Clipboard.SetDataObject(path, true);
            SetStatus($"Copied: {path}");
        }
        catch (Exception ex) { SetStatus($"Error getting path: {ex.Message}"); }
    }

    // ──────────────────────────── XPath Tool ────────────────────────────
    private void XpathInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return && e.KeyboardDevice.Modifiers == ModifierKeys.Control)
        { ExecuteXPath(); e.Handled = true; }
    }

    private void ExecuteXPath_Click(object sender, RoutedEventArgs e) => ExecuteXPath();

    private void ExecuteXPath()
    {
        var tab = ActiveTab;
        if (tab is null) return;

        var expression = xpathInput.Text?.Trim();
        if (string.IsNullOrEmpty(expression))
        { SetStatus("Enter an XPath expression first."); return; }

        if (tab.FileType != FileType.Xml && tab.FileType != FileType.Json)
        { SetStatus("Please open an XML or JSON file first."); return; }

        if (string.IsNullOrWhiteSpace(tab.Editor.Text))
        { SetStatus("The editor is empty."); return; }

        bool isJson = tab.FileType == FileType.Json;
        try
        {
            var results = isJson
                ? JsonService.ExecuteJsonPath(tab.Editor.Text, expression)
                : XmlService.ExecuteXPath(tab.Editor.Text, expression);
            xpathResultsList.ItemsSource = results;
            string kind = isJson ? "JSONPath" : "XPath";
            xpathResultsHeader.Text = results.Count == 0
                ? "Results:  (no matches)"
                : $"Results:  {results.Count} node(s) found";
            SetStatus(results.Count == 0
                ? $"{kind} executed — no matching nodes."
                : $"{kind} executed — {results.Count} match(es).");
        }
        catch (Exception ex)
        {
            xpathResultsList.ItemsSource = null;
            xpathResultsHeader.Text = "Results:";
            string kind = isJson ? "JSONPath" : "XPath";
            SetStatus($"{kind} error: {ex.Message}");
            MessageBox.Show(ex.Message, $"{kind} Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void XpathResult_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (xpathResultsList.SelectedItem is not XPathResultItem item) return;

        var tab = ActiveTab;
        if (tab is null) return;

        int line = item.LineNumber;
        if (line < 1 || line > tab.Editor.Document.LineCount) return;

        // Highlight the line in the background renderer immediately (no focus needed)
        tab.HighlightRenderer.Line = line;
        tab.Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);

        // Defer focus + selection + scroll until after the ListBox click event finishes,
        // so the editor actually has keyboard focus when Select() is called.
        Dispatcher.InvokeAsync(() =>
        {
            try
            {
                tab.Editor.Focus();

                var docLine = tab.Editor.Document.GetLineByNumber(line);
                tab.Editor.Select(docLine.Offset, docLine.Length);
                tab.Editor.ScrollTo(line, 1);

                SetStatus($"Navigated to line {line}  —  {item.XPath}");
            }
            catch { }
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    // ──────────────────────────── inline editor view toggle ────────────────────────────
    private FrameworkElement BuildTabViewContainer(EditorTab state)
    {
        var tv = CreateInlineTreeView();
        tv.Visibility = Visibility.Collapsed;
        state.InlineTreeView = tv;

        var contentLayer = new Grid();
        contentLayer.Children.Add(state.Editor);
        contentLayer.Children.Add(tv);

        var textBtn = MakeViewToggleButton("Text", active: true);
        var gridBtn = MakeViewToggleButton("Grid", active: false);
        state.TextViewButton = textBtn;
        state.GridViewButton = gridBtn;

        textBtn.Click += (s, e) => SwitchToTextView(state);
        gridBtn.Click += (s, e) => SwitchToGridView(state);

        var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0, 0, 0) };
        btnPanel.Children.Add(textBtn);
        btnPanel.Children.Add(gridBtn);

        var bottomBar = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x26)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(2, 2, 0, 2),
            Child = btnPanel
        };

        var container = new Grid();
        container.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        container.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(contentLayer, 0);
        Grid.SetRow(bottomBar, 1);
        container.Children.Add(contentLayer);
        container.Children.Add(bottomBar);
        return container;
    }

    private static TreeView CreateInlineTreeView()
    {
        var tv = new TreeView { BorderThickness = new Thickness(0), Background = Brushes.Transparent };
        VirtualizingPanel.SetIsVirtualizing(tv, true);
        VirtualizingPanel.SetVirtualizationMode(tv, VirtualizationMode.Recycling);

        tv.Resources[SystemColors.HighlightBrushKey] = new SolidColorBrush(Color.FromRgb(0x09, 0x47, 0x71));
        tv.Resources[SystemColors.HighlightTextBrushKey] = Brushes.White;
        tv.Resources[SystemColors.InactiveSelectionHighlightBrushKey] = new SolidColorBrush(Color.FromRgb(0x2A, 0x4A, 0x6A));
        tv.Resources[SystemColors.InactiveSelectionHighlightTextBrushKey] = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));

        const string itemStyleXaml = """
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                   TargetType="TreeViewItem">
                <Setter Property="IsExpanded" Value="{Binding IsExpanded}"/>
                <Setter Property="Foreground" Value="#D4D4D4"/>
                <Setter Property="Padding"    Value="2,1"/>
            </Style>
            """;
        tv.ItemContainerStyle = (Style)XamlReader.Parse(itemStyleXaml);

        const string templateXaml = """
            <HierarchicalDataTemplate
                xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                ItemsSource="{Binding Children}">
                <StackPanel Orientation="Horizontal" Margin="0,1">
                    <TextBlock Text="{Binding Label}"        Foreground="{Binding LabelColor}"
                               FontWeight="SemiBold" FontFamily="Consolas" FontSize="12"/>
                    <TextBlock Text="{Binding ValueDisplay}" Foreground="{Binding ValueColor}"
                               Margin="8,0,0,0" FontFamily="Consolas" FontSize="12"/>
                </StackPanel>
            </HierarchicalDataTemplate>
            """;
        tv.ItemTemplate = (HierarchicalDataTemplate)XamlReader.Parse(templateXaml);
        return tv;
    }

    private static Button MakeViewToggleButton(string label, bool active)
    {
        var btn = new Button
        {
            Content = label,
            FontSize = 11,
            FontFamily = new FontFamily("Segoe UI"),
            Padding = new Thickness(10, 2, 10, 2),
            Margin = new Thickness(0, 0, 2, 0),
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand
        };
        SetViewButtonActive(btn, active);
        return btn;
    }

    private static void SetViewButtonActive(Button btn, bool active)
    {
        btn.Background = new SolidColorBrush(active
            ? Color.FromRgb(0x00, 0x7A, 0xCC)
            : Color.FromRgb(0x3C, 0x3C, 0x3C));
        btn.Foreground = active
            ? Brushes.White
            : new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
    }

    private void SwitchToTextView(EditorTab tab)
    {
        tab.IsGridMode = false;
        tab.Editor.Visibility = Visibility.Visible;
        if (tab.InlineTreeView is not null) tab.InlineTreeView.Visibility = Visibility.Collapsed;
        if (tab.TextViewButton is not null) SetViewButtonActive(tab.TextViewButton, true);
        if (tab.GridViewButton is not null) SetViewButtonActive(tab.GridViewButton, false);
    }

    private void SwitchToGridView(EditorTab tab)
    {
        if (tab.InlineTreeView is null) return;
        bool ok = tab.FileType == FileType.Xml || tab.FileType == FileType.Json;
        if (!ok) { SwitchToTextView(tab); return; }

        try
        {
            tab.InlineTreeView.ItemsSource = tab.FileType switch
            {
                FileType.Xml => BuildXmlGrid(tab.Editor.Text),
                FileType.Json => BuildJsonGrid(tab.Editor.Text),
                _ => null
            };
        }
        catch { tab.InlineTreeView.ItemsSource = null; }

        tab.IsGridMode = true;
        tab.Editor.Visibility = Visibility.Collapsed;
        tab.InlineTreeView.Visibility = Visibility.Visible;
        if (tab.TextViewButton is not null) SetViewButtonActive(tab.TextViewButton, false);
        if (tab.GridViewButton is not null) SetViewButtonActive(tab.GridViewButton, true);
    }

    private static List<GridNode> BuildXmlGrid(string xmlText)
    {
        try
        {
            var doc = XDocument.Parse(xmlText);
            if (doc.Root is null) return [];
            return [BuildXmlElement(doc.Root, 0)];
        }
        catch (Exception ex)
        {
            return [new GridNode { Label = "Parse error", ValueDisplay = ex.Message, LabelColor = "#F48771" }];
        }
    }

    private static GridNode BuildXmlElement(XElement el, int depth)
    {
        bool hasChildElements = el.HasElements;
        string leafValue = !hasChildElements && !string.IsNullOrWhiteSpace(el.Value)
            ? Truncate(el.Value.Trim()) : "";

        var node = new GridNode
        {
            Label = el.Name.LocalName,
            LabelColor = "#4EC9B0",
            ValueDisplay = leafValue,
            ValueColor = "#CE9178",
            IsExpanded = depth <= 1
        };

        foreach (var attr in el.Attributes())
            node.Children.Add(new GridNode
            {
                Label = $"@{attr.Name.LocalName}",
                LabelColor = "#9CDCFE",
                ValueDisplay = Truncate(attr.Value),
                ValueColor = "#CE9178",
                IsExpanded = false
            });

        foreach (var child in el.Elements())
            node.Children.Add(BuildXmlElement(child, depth + 1));

        return node;
    }

    private static List<GridNode> BuildJsonGrid(string jsonText)
    {
        try
        {
            var root = JToken.Parse(jsonText);
            string rootLabel = root.Type == JTokenType.Array ? $"[{((JArray)root).Count}]"
                             : root.Type == JTokenType.Object ? "{}"
                             : "value";
            return [BuildJsonToken(rootLabel, root, 0)];
        }
        catch (Exception ex)
        {
            return [new GridNode { Label = "Parse error", ValueDisplay = ex.Message, LabelColor = "#F48771" }];
        }
    }

    private static GridNode BuildJsonToken(string name, JToken token, int depth)
    {
        if (token is JObject obj)
        {
            var node = new GridNode { Label = name, LabelColor = "#4EC9B0", ValueDisplay = $"{{{obj.Count}}}", IsExpanded = depth <= 1 };
            foreach (var prop in obj.Properties())
                node.Children.Add(BuildJsonToken(prop.Name, prop.Value, depth + 1));
            return node;
        }
        if (token is JArray arr)
        {
            var node = new GridNode { Label = name, LabelColor = "#DCDCAA", ValueDisplay = $"[{arr.Count}]", IsExpanded = depth <= 1 };
            for (int i = 0; i < arr.Count; i++)
                node.Children.Add(BuildJsonToken($"[{i}]", arr[i], depth + 1));
            return node;
        }
        return new GridNode
        {
            Label = name,
            LabelColor = "#9CDCFE",
            ValueDisplay = Truncate(token.ToString()),
            ValueColor = token.Type switch
            {
                JTokenType.String => "#CE9178",
                JTokenType.Null => "#808080",
                JTokenType.Boolean => "#569CD6",
                _ => "#B5CEA8"
            },
            IsExpanded = false
        };
    }

    private static string Truncate(string s, int max = 200)
        => s.Length <= max ? s : s[..max] + "…";

    // ──────────────────────────── helpers ────────────────────────────
    private static FileType DetectFileType(string filePath, string content)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (ext is ".xml" or ".xsd" or ".xsl" or ".xslt") return FileType.Xml;
        if (ext == ".json") return FileType.Json;

        var t = content.TrimStart();
        if (t.StartsWith('<')) return FileType.Xml;
        if (t.StartsWith('{') || t.StartsWith('[')) return FileType.Json;
        return FileType.None;
    }

    private void ApplyFileTypeToTab(EditorTab tab, FileType type)
    {
        tab.FileType = type;
        tab.Editor.SyntaxHighlighting = type switch
        {
            FileType.Xml => GetOrCreateXmlHighlighting(),
            FileType.Json => GetOrCreateJsonHighlighting(),
            _ => null
        };
        UpdateRightPanelForTab(tab);
        UpdateStatusBar(tab);
    }

    private static IHighlightingDefinition? GetOrCreateXmlHighlighting()
    {
        if (_xmlHighlighting is not null) return _xmlHighlighting;

        const string xshd = """
            <?xml version="1.0"?>
            <SyntaxDefinition name="XmlVivid"
                xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
              <Color name="Comment"      foreground="#6A9955" fontStyle="italic"/>
              <Color name="CData"        foreground="#CE9178"/>
              <Color name="DocType"      foreground="#808080"/>
              <Color name="XmlDecl"      foreground="#808080"/>
              <Color name="TagName"      foreground="#4EC9B0" fontWeight="bold"/>
              <Color name="AttrName"     foreground="#9CDCFE"/>
              <Color name="AttrValue"    foreground="#F0A070"/>
              <Color name="Entity"       foreground="#DCDCAA"/>
              <Color name="BracketPunct" foreground="#808080"/>
              <RuleSet ignoreCase="false">
                <!-- Comments -->
                <Span color="Comment" multiline="true">
                  <Begin>&lt;!--</Begin>
                  <End>--&gt;</End>
                </Span>
                <!-- CDATA -->
                <Span color="CData" multiline="true">
                  <Begin>&lt;!\[CDATA\[</Begin>
                  <End>\]\]&gt;</End>
                </Span>
                <!-- DOCTYPE -->
                <Span color="DocType" multiline="true">
                  <Begin>&lt;!DOCTYPE</Begin>
                  <End>&gt;</End>
                </Span>
                <!-- XML declaration -->
                <Span color="XmlDecl" multiline="true">
                  <Begin>&lt;\?</Begin>
                  <End>\?&gt;</End>
                </Span>
                <!-- Tags with attribute coloring inside -->
                <Span multiline="true">
                  <Begin color="BracketPunct">&lt;/?</Begin>
                  <End color="BracketPunct">/?>(?=\s|&gt;|$)</End>
                  <RuleSet>
                    <!-- attribute name -->
                    <Rule color="AttrName">[a-zA-Z_][\w:.-]*(?=\s*=)</Rule>
                    <!-- double-quoted attribute value -->
                    <Span color="AttrValue">
                      <Begin>"</Begin>
                      <End>"</End>
                    </Span>
                    <!-- single-quoted attribute value -->
                    <Span color="AttrValue">
                      <Begin>'</Begin>
                      <End>'</End>
                    </Span>
                    <!-- tag name (first word after < or </) -->
                    <Rule color="TagName">[a-zA-Z_][\w:.-]*</Rule>
                  </RuleSet>
                </Span>
                <!-- Entities -->
                <Rule color="Entity">&amp;[a-zA-Z]+;|&amp;#[0-9]+;|&amp;#x[0-9a-fA-F]+;</Rule>
              </RuleSet>
            </SyntaxDefinition>
            """;

        try
        {
            using var reader = new XmlTextReader(new StringReader(xshd));
            _xmlHighlighting = HighlightingLoader.Load(reader, HighlightingManager.Instance);
        }
        catch { _xmlHighlighting = null; }

        return _xmlHighlighting;
    }

    private static IHighlightingDefinition? GetOrCreateJsonHighlighting()
    {
        if (_jsonHighlighting is not null) return _jsonHighlighting;

        // Use Rule-based XSHD — Rules use full .NET regex (lookahead supported),
        // whereas Span End patterns do not. Key rule must come BEFORE String rule.
        const string xshd = """
            <?xml version="1.0"?>
            <SyntaxDefinition name="Json"
                xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
              <Color name="Key"     foreground="#9CDCFE"/>
              <Color name="String"  foreground="#F0A070"/>
              <Color name="Number"  foreground="#B5CEA8"/>
              <Color name="Keyword" foreground="#DCDCAA"/>
              <RuleSet ignoreCase="false">
                <!-- JSON object key: "text" followed by colon (lookahead works in Rules) -->
                <Rule color="Key">"(?:[^"\\]|\\.)*"(?=\s*:)</Rule>
                <!-- JSON string value (keys already consumed above) -->
                <Rule color="String">"(?:[^"\\]|\\.)*"</Rule>
                <!-- numbers -->
                <Rule color="Number">-?(0|[1-9][0-9]*)(\.[0-9]+)?([eE][+-]?[0-9]+)?</Rule>
                <!-- keywords -->
                <Rule color="Keyword">\b(true|false|null)\b</Rule>
              </RuleSet>
            </SyntaxDefinition>
            """;

        try
        {
            using var reader = new XmlTextReader(new StringReader(xshd));
            _jsonHighlighting = HighlightingLoader.Load(reader, HighlightingManager.Instance);
        }
        catch { _jsonHighlighting = null; }

        return _jsonHighlighting;
    }

    private void SetStatus(string message) => statusText.Text = message;

    private void ClearXPathResults()
    {
        xpathResultsList.ItemsSource = null;
        xpathResultsHeader.Text = "Results:";
    }

    private static void ShowError(string message) =>
        MessageBox.Show(message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
}
