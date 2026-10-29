using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Margin.Core;
using Margin.Core.Storage;

namespace Margin.App;

/// <summary>
/// The reader: a page on the left, the companion on the right. The page counts as read when it is
/// shown, if the reader got there by reading on (not by jumping ahead in the table of contents); the
/// furthest such page is the horizon, and everything the companion does is cut at it.
/// </summary>
public sealed class MainWindow : Window
{
    static readonly IBrush Paper = new SolidColorBrush(Color.FromRgb(0xFB, 0xF8, 0xF1));
    static readonly IBrush Ink = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22));
    static readonly IBrush Muted = new SolidColorBrush(Color.FromRgb(0x6B, 0x66, 0x5E));
    static readonly IBrush Accent = new SolidColorBrush(Color.FromRgb(0x8A, 0x4B, 0x1F));

    readonly Library lib;
    readonly Companion companion = new();
    readonly ReaderMeasure measure = new(18);
    readonly PageView pageView = new();
    readonly Border pageFrame;
    readonly TextBlock status = new() { Foreground = Muted, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock serviceStatus = new() { Foreground = Muted, VerticalAlignment = VerticalAlignment.Center };
    readonly ComboBox toc = new() { MinWidth = 260 };
    readonly ComboBox books = new() { MinWidth = 260 };
    readonly Slider progressBar = new() { Minimum = 0, Maximum = 1, IsHitTestVisible = false, Height = 20 };
    readonly TabControl tabs = new();

    // The companion panel's parts.
    readonly TextBlock selectionView = new() { TextWrapping = TextWrapping.Wrap, FontStyle = FontStyle.Italic, Foreground = Muted, MaxHeight = 120 };
    readonly TextBox question = new() { Watermark = "Ask about the book or the selection: who is this? what does it mean?", AcceptsReturn = false };
    readonly StackPanel answers = new() { Spacing = 10 };
    readonly StackPanel chapterList = new() { Spacing = 8 };
    readonly StackPanel characterList = new() { Spacing = 6 };
    readonly StackPanel wordList = new() { Spacing = 6 };
    readonly StackPanel cardPanel = new() { Spacing = 10 };
    readonly StackPanel noteList = new() { Spacing = 8 };

    Book? book;
    List<Page> pages = [];
    int pageIndex;
    int horizon;
    (int Start, int End, string Text)? selection;
    bool showingChapter;  // the table of contents follows the page; that is not a jump

    public MainWindow()
    {
        Title = "Margin";
        Width = 1320;
        Height = 860;
        Background = Paper;
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Margin");
        Directory.CreateDirectory(dir);
        lib = new Library(Path.Combine(dir, "library.db"));

        var open = new Button { Content = "Open book…" };
        open.Click += async (_, _) => await OpenDialog();
        books.SelectionChanged += (_, _) =>
        {
            if (books.SelectedItem is ComboBoxItem { Tag: BookRow r } && r.Id != book?.Id) Load(r.Path);
        };
        toc.SelectionChanged += (_, _) =>
        {
            if (!showingChapter && toc.SelectedItem is ComboBoxItem { Tag: Chapter c } && book is not null) GoTo(Paginator.PageAt(pages, c.Start), reading: false);
        };
        var top = new DockPanel { Margin = new Thickness(16, 10), LastChildFill = true };
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        left.Children.AddRange([open, books, toc]);
        DockPanel.SetDock(left, Dock.Left);
        DockPanel.SetDock(serviceStatus, Dock.Right);
        top.Children.AddRange([left, serviceStatus, new Border()]);

        pageFrame = new Border { Child = pageView, Padding = new Thickness(56, 36), Background = Brushes.White, BoxShadow = BoxShadows.Parse("0 1 6 0 #22000000"), CornerRadius = new CornerRadius(4), ClipToBounds = true };
        var prev = new Button { Content = "‹ Previous" };
        var next = new Button { Content = "Next ›" };
        prev.Click += (_, _) => Turn(-1);
        next.Click += (_, _) => Turn(+1);
        var markRead = new Button { Content = "Count as read up to here" };
        markRead.Click += (_, _) => MarkReadHere();
        var bottom = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var nav = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        nav.Children.AddRange([prev, next, markRead]);
        DockPanel.SetDock(nav, Dock.Right);
        bottom.Children.AddRange([nav, status]);
        var reader = new DockPanel { Margin = new Thickness(16, 0, 8, 16) };
        DockPanel.SetDock(bottom, Dock.Bottom);
        DockPanel.SetDock(progressBar, Dock.Bottom);
        reader.Children.AddRange([bottom, progressBar, pageFrame]);
        pageFrame.SizeChanged += (_, _) => Repaginate();
        pageView.Selected += (s, e, t) =>
        {
            selection = (s, e, t);
            selectionView.Text = $"“{(t.Length > 400 ? t[..400] + "…" : t)}”";
        };

        BuildCompanion();
        var side = new Border { Child = tabs, Width = 440, Margin = new Thickness(8, 0, 16, 16), Background = Brushes.White, CornerRadius = new CornerRadius(4), Padding = new Thickness(12) };
        var body = new DockPanel();
        DockPanel.SetDock(side, Dock.Right);
        body.Children.AddRange([side, reader]);
        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        root.Children.AddRange([top, body]);
        Content = root;

        KeyDown += (_, e) =>
        {
            if (FocusManager?.GetFocusedElement() is TextBox) return;
            if (e.Key is Key.Right or Key.PageDown or Key.Space) Turn(+1);
            else if (e.Key is Key.Left or Key.PageUp) Turn(-1);
        };
        Opened += async (_, _) => await Start();
    }

    // --- the companion panel ---------------------------------------------------------------------

    static ScrollViewer Scroll(Control c) => new() { Content = c, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };

    static TextBlock Text(string s, double size = 14, IBrush? fg = null, FontWeight weight = FontWeight.Normal) =>
        new() { Text = s, FontSize = size, Foreground = fg ?? Ink, FontWeight = weight, TextWrapping = TextWrapping.Wrap };

    static TabItem Tab(string header, Control content) => new() { Header = header, Content = content, FontSize = 16, Padding = new Thickness(8, 4) };

    void BuildCompanion()
    {
        var ask = new Button { Content = "Ask", IsDefault = true };
        ask.Click += async (_, _) => await Ask();
        var note = new Button { Content = "Note the selection" };
        note.Click += (_, _) => AddNote();
        var askRow = new DockPanel();
        DockPanel.SetDock(ask, Dock.Right);
        askRow.Children.AddRange([ask, question]);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(note);
        var askHead = new StackPanel { Spacing = 8 };
        askHead.Children.AddRange([Text("Selection", 12, Muted), selectionView, askRow, buttons]);
        var askTab = new DockPanel();
        DockPanel.SetDock(askHead, Dock.Top);
        askTab.Children.AddRange([askHead, Scroll(answers)]);

        var refreshChars = new Button { Content = "Refresh" };
        refreshChars.Click += async (_, _) => await LoadCharacters();
        var charTab = new DockPanel();
        DockPanel.SetDock(refreshChars, Dock.Top);
        charTab.Children.AddRange([refreshChars, Scroll(characterList)]);

        var refreshWords = new Button { Content = "Rare words in this chapter" };
        refreshWords.Click += async (_, _) => await LoadWords();
        var wordTab = new DockPanel();
        DockPanel.SetDock(refreshWords, Dock.Top);
        wordTab.Children.AddRange([refreshWords, Scroll(wordList)]);

        tabs.ItemsSource = new[]
        {
            Tab("Ask", askTab),
            Tab("Chapters", Scroll(chapterList)),
            Tab("Characters", charTab),
            Tab("Words", wordTab),
            Tab("Cards", Scroll(cardPanel)),
            Tab("Notes", Scroll(noteList)),
        };
        tabs.SelectionChanged += async (_, _) =>
        {
            switch ((tabs.SelectedItem as TabItem)?.Header as string)
            {
                case "Chapters": ShowChapters(); break;
                case "Characters": await LoadCharacters(); break;
                case "Words": await LoadWords(); break;
                case "Cards": ShowCard(); break;
                case "Notes": ShowNotes(); break;
            }
        };
    }

    async Task Ask()
    {
        if (book is null || string.IsNullOrWhiteSpace(question.Text)) return;
        string q = question.Text.Trim();
        question.Text = "";
        var sel = selection is { } s ? new Span(s.Start, s.End) : null;
        var card = new StackPanel { Spacing = 4 };
        card.Children.Add(Text(q, 14, Accent, FontWeight.SemiBold));
        var a = Text("…", 14, Muted);
        card.Children.Add(a);
        answers.Children.Insert(0, card);
        try
        {
            var r = await companion.Ask(book, horizon, q, sel);
            a.Text = r.Text;
            a.Foreground = r.Withheld ? Muted : Ink;
            if (r.Withheld) a.FontStyle = FontStyle.Italic;
            lib.AddQuestion(book.Id, horizon, selection?.Text ?? "", q, r.Text, r.Withheld);
        }
        catch (Exception e)
        {
            a.Text = $"The companion is not answering ({e.Message}). Is the service running?";
        }
    }

    void AddNote()
    {
        if (book is null || selection is not { } s) return;
        lib.AddNote(book.Id, s.Start, s.End, s.Text, question.Text ?? "");
        question.Text = "";
        status.Text = "Noted.";
    }

    void ShowChapters()
    {
        chapterList.Children.Clear();
        if (book is null) return;
        var done = book.Chapters.Where(c => c.End <= horizon && c.End - c.Start > 200).ToList();
        if (done.Count == 0) chapterList.Children.Add(Text("Finish a chapter and its summary appears here.", 14, Muted));
        Func<Task>? latest = null;
        foreach (var c in done)
        {
            var body = Text("", 13);
            var head = new Button { Content = c.Title, HorizontalAlignment = HorizontalAlignment.Stretch, Background = Brushes.Transparent };
            async Task Open()
            {
                body.Text = "…";
                try { body.Text = await companion.Summary(book, horizon, c.Index) ?? "Not finished yet."; }
                catch (Exception e) { body.Text = $"The companion is not answering ({e.Message})."; }
            }
            head.Click += async (_, _) => await Open();
            latest = Open;
            var item = new StackPanel { Spacing = 2 };
            item.Children.AddRange([head, body]);
            chapterList.Children.Add(item);
        }
        // The chapter just finished is the one a returning reader wants recalled.
        if (latest is not null) _ = latest();
    }

    async Task LoadCharacters()
    {
        characterList.Children.Clear();
        if (book is null) return;
        characterList.Children.Add(Text($"Who you have met in the first {100.0 * horizon / book.Length:0}% of the book.", 12, Muted));
        try
        {
            foreach (var c in (await companion.Characters(book, horizon)).Take(40))
            {
                var item = new StackPanel { Spacing = 1 };
                item.Children.Add(Text(c.Name, 15, Ink, FontWeight.SemiBold));
                string also = c.Aliases.Count > 0 ? $" · also {string.Join(", ", c.Aliases.Take(3))}" : "";
                item.Children.Add(Text($"{c.Mentions} mentions · first in {book.Chapters[c.Chapter].Title}{also}", 12, Muted));
                var links = c.Links.Take(4).Select(l => l.Name).ToList();
                if (links.Count > 0) item.Children.Add(Text("Often with " + string.Join(", ", links), 12, Muted));
                characterList.Children.Add(item);
            }
        }
        catch (Exception e) { characterList.Children.Add(Text($"The companion is not answering ({e.Message}).", 13, Muted)); }
    }

    async Task LoadWords()
    {
        wordList.Children.Clear();
        if (book is null) return;
        int ch = book.ChapterAt(Math.Max(0, Math.Min(horizon, pages[pageIndex].Start))).Index;
        try
        {
            foreach (var w in await companion.Vocabulary(book, horizon, ch))
            {
                var add = new Button { Content = "Add card", FontSize = 12 };
                var meaning = Text("", 12, Muted);
                add.Click += async (_, _) =>
                {
                    add.IsEnabled = false;
                    meaning.Text = "…";
                    try
                    {
                        meaning.Text = await companion.Meaning(w.Word, w.Context);
                        lib.AddCard(book.Id, w.Word, meaning.Text, w.Context, w.Position);
                        add.Content = "Added";
                    }
                    catch (Exception e) { meaning.Text = e.Message; add.IsEnabled = true; }
                };
                var row = new DockPanel();
                DockPanel.SetDock(add, Dock.Right);
                var words = new StackPanel();
                words.Children.AddRange([Text(w.Word, 15, Ink, FontWeight.SemiBold), Text(w.Context, 12, Muted), meaning]);
                row.Children.AddRange([add, words]);
                wordList.Children.Add(row);
            }
        }
        catch (Exception e) { wordList.Children.Add(Text($"The companion is not answering ({e.Message}).", 13, Muted)); }
    }

    void ShowCard()
    {
        cardPanel.Children.Clear();
        var due = lib.DueCards(1);
        if (due.Count == 0)
        {
            cardPanel.Children.Add(Text("No cards due. Add words from the Words tab.", 14, Muted));
            return;
        }
        var c = due[0];
        cardPanel.Children.Add(Text(c.Word, 26, Ink, FontWeight.SemiBold));
        cardPanel.Children.Add(Text(c.Context.Replace(c.Word, "____", StringComparison.OrdinalIgnoreCase), 14, Muted));
        var answer = Text(c.Meaning, 15);
        answer.IsVisible = false;
        var grades = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, IsVisible = false };
        foreach (var (label, g) in new[] { ("Again", 1), ("Hard", 3), ("Good", 4), ("Easy", 5) })
        {
            var b = new Button { Content = label };
            b.Click += (_, _) => { lib.Review(c.Id, g); ShowCard(); };
            grades.Children.Add(b);
        }
        var show = new Button { Content = "Show the meaning" };
        show.Click += (_, _) => { answer.IsVisible = grades.IsVisible = true; show.IsVisible = false; };
        cardPanel.Children.AddRange([show, answer, grades]);
    }

    void ShowNotes()
    {
        noteList.Children.Clear();
        if (book is null) return;
        foreach (var n in lib.Notes(book.Id))
        {
            var go = new Button { Content = book.ChapterAt(n.Start).Title, FontSize = 12 };
            go.Click += (_, _) => GoTo(Paginator.PageAt(pages, n.Start), reading: n.Start <= horizon);
            var item = new StackPanel();
            item.Children.AddRange([go, Text($"“{n.Quote}”", 13, Muted)]);
            if (n.Text.Length > 0) item.Children.Add(Text(n.Text, 14));
            noteList.Children.Add(item);
        }
        if (noteList.Children.Count == 0) noteList.Children.Add(Text("Select text and press “Note the selection”.", 14, Muted));
    }

    // --- books and pages ---------------------------------------------------------------------------

    async Task Start()
    {
        RefreshBooks();
        var o = Program.Options;
        if (o.TryGetValue("book", out var path)) Load(path);
        else if (lib.Books().FirstOrDefault() is { } last && File.Exists(last.Path)) Load(last.Path);
        _ = Task.Run(async () =>
        {
            bool alive = await Service.Ensure(companion);
            Dispatcher.UIThread.Post(() => serviceStatus.Text = alive ? "Companion ready" : "Companion offline: start service/ (see README)");
        });
        // The pages exist once the window has its size.
        for (int i = 0; i < 50 && book is not null && pages.Count == 0; i++) await Task.Delay(100);
        if (o.TryGetValue("at", out var at) && book is not null && double.TryParse(at, System.Globalization.CultureInfo.InvariantCulture, out double f))
        {
            // Screenshots: everything up to there counts as read.
            int target = Paginator.PageAt(pages, (int)(f * book.Length));
            for (int p = pageIndex; p <= target; p++) GoTo(p, reading: true);
        }
        if (o.TryGetValue("select", out var sel)) pageView.Select(sel);
        if (o.TryGetValue("tab", out var tab))
            tabs.SelectedItem = tabs.Items.OfType<TabItem>().FirstOrDefault(t => string.Equals(t.Header as string, tab, StringComparison.OrdinalIgnoreCase));
        if (o.TryGetValue("ask", out var q))
        {
            await Service.Ensure(companion);
            question.Text = q;
            await Ask();
        }
        if (o.TryGetValue("screenshot", out var shot))
        {
            double wait = o.TryGetValue("wait", out var w) && double.TryParse(w, System.Globalization.CultureInfo.InvariantCulture, out double ws) ? ws : 1.5;
            await Task.Delay(TimeSpan.FromSeconds(wait));
            var size = new PixelSize((int)Bounds.Width, (int)Bounds.Height);
            using var bmp = new Avalonia.Media.Imaging.RenderTargetBitmap(size);
            bmp.Render(this);
            bmp.Save(shot);
            Close();
        }
    }

    async Task OpenDialog()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open a book",
            FileTypeFilter = [new FilePickerFileType("EPUB books") { Patterns = ["*.epub"] }],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } p) Load(p);
    }

    void RefreshBooks()
    {
        books.ItemsSource = lib.Books().Select(r => new ComboBoxItem { Content = $"{r.Title} — {r.Author}", Tag = r }).ToList();
    }

    void Load(string path)
    {
        try
        {
            book = EpubReader.Read(path);
        }
        catch (Exception e)
        {
            status.Text = $"Cannot open {Path.GetFileName(path)}: {e.Message}";
            return;
        }
        lib.AddBook(book, Path.GetFullPath(path));
        lib.Opened(book.Id);
        (int position, horizon) = lib.Progress(book.Id);
        Title = $"{book.Title} — Margin";
        RefreshBooks();
        books.SelectedItem = books.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (i.Tag as BookRow)?.Id == book.Id);
        toc.ItemsSource = book.Chapters.Where(c => c.End - c.Start > 0).Select(c => new ComboBoxItem { Content = c.Title, Tag = c }).ToList();
        answers.Children.Clear();
        foreach (var q in lib.Questions(book.Id).AsEnumerable().Reverse().Take(20))
        {
            var card = new StackPanel { Spacing = 4 };
            card.Children.AddRange([Text(q.Text, 14, Accent, FontWeight.SemiBold), Text(q.Answer, 14, q.Withheld ? Muted : Ink)]);
            answers.Children.Add(card);
        }
        pages = [];
        Repaginate(position);
    }

    void Repaginate(int? at = null)
    {
        if (book is null) return;
        double w = pageFrame.Bounds.Width - pageFrame.Padding.Left - pageFrame.Padding.Right;
        double h = pageFrame.Bounds.Height - pageFrame.Padding.Top - pageFrame.Padding.Bottom;
        if (w < 100 || h < 100) return;
        int keep = at ?? (pages.Count > 0 ? pages[pageIndex].Start : 0);
        pages = Paginator.Paginate(book, measure, w, h);
        GoTo(Paginator.PageAt(pages, keep), reading: false);
    }

    void Turn(int delta)
    {
        if (pages.Count == 0) return;
        int target = Math.Clamp(pageIndex + delta, 0, pages.Count - 1);
        // Reading on: a page right after what was read counts as read.
        GoTo(target, reading: delta > 0 && pages[target].Start <= horizon + 1);
    }

    void MarkReadHere()
    {
        if (book is null) return;
        horizon = lib.Read(book.Id, pages[pageIndex].Start, pages[pageIndex].End);
        UpdateStatus();
    }

    void GoTo(int index, bool reading)
    {
        if (book is null || pages.Count == 0) return;
        pageIndex = Math.Clamp(index, 0, pages.Count - 1);
        var page = pages[pageIndex];
        pageView.Show(book, page, measure);
        selection = null;
        selectionView.Text = "";
        if (reading || page.End <= horizon) horizon = lib.Read(book.Id, page.Start, Math.Max(horizon, reading ? page.End : horizon));
        else lib.Jump(book.Id, page.Start);
        UpdateStatus();
    }

    void UpdateStatus()
    {
        if (book is null) return;
        var page = pages[pageIndex];
        var ch = book.ChapterAt(page.Start);
        showingChapter = true;
        toc.SelectedItem = toc.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (i.Tag as Chapter)?.Index == ch.Index);
        showingChapter = false;
        progressBar.Value = (double)horizon / book.Length;
        string ahead = page.Start > horizon ? " · ahead of what you have read: the companion knows nothing from here" : "";
        status.Text = $"Page {pageIndex + 1} of {pages.Count} · {ch.Title} · read {100.0 * horizon / book.Length:0}%{ahead}";
    }

    protected override void OnClosed(EventArgs e)
    {
        lib.Dispose();
        Service.Stop();
        base.OnClosed(e);
    }
}
