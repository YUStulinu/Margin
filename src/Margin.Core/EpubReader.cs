using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using HtmlAgilityPack;

namespace Margin.Core;

/// <summary>
/// Reads an EPUB (2 or 3) into a <see cref="Book"/>: the package file for the metadata and the spine
/// (reading order), the navigation document (EPUB 3) or NCX (EPUB 2) for the chapters, and each XHTML
/// document's headings and paragraphs as blocks. Project Gutenberg's licence header and footer are
/// left out: they are not the book, and a question about the book should not find them.
/// </summary>
public static partial class EpubReader
{
    static readonly XNamespace Opf = "http://www.idpf.org/2007/opf";
    static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    static readonly XNamespace Ncx = "http://www.daisy.org/z3986/2005/ncx/";
    static readonly XNamespace Container = "urn:oasis:names:tc:opendocument:xmlns:container";

    static readonly HashSet<string> BlockTags = ["p", "h1", "h2", "h3", "h4", "h5", "h6", "li", "dt", "dd", "pre", "td", "th", "blockquote", "div"];
    static readonly HashSet<string> Skipped = ["script", "style", "head", "title"];

    public static Book Read(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        return Read(zip);
    }

    public static Book Read(Stream stream)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        return Read(zip);
    }

    static Book Read(ZipArchive zip)
    {
        var container = XDocument.Load(Entry(zip, "META-INF/container.xml"));
        string opfPath = container.Descendants(Container + "rootfile").First().Attribute("full-path")!.Value;
        string opfDir = DirOf(opfPath);
        var opf = XDocument.Load(Entry(zip, opfPath));
        var meta = opf.Root!.Element(Opf + "metadata");
        string title = meta?.Element(Dc + "title")?.Value.Trim() ?? "Untitled";
        string author = meta?.Element(Dc + "creator")?.Value.Trim() ?? "";

        var manifest = opf.Root.Element(Opf + "manifest")!.Elements(Opf + "item").ToDictionary(
            i => i.Attribute("id")!.Value,
            i => (Href: Combine(opfDir, Uri.UnescapeDataString(i.Attribute("href")!.Value)),
                  Type: i.Attribute("media-type")?.Value ?? "",
                  Props: i.Attribute("properties")?.Value ?? ""));
        var spineEl = opf.Root.Element(Opf + "spine")!;
        var spine = spineEl.Elements(Opf + "itemref")
            .Where(r => r.Attribute("linear")?.Value != "no")
            .Select(r => manifest[r.Attribute("idref")!.Value].Href)
            .ToList();

        // Text, in reading order, with where each anchor (file#id) lands.
        var blocks = new List<(BlockKind Kind, string Text)>();
        var anchors = new Dictionary<string, int>();  // "file" or "file#id" -> block index
        foreach (var file in spine)
        {
            var e = zip.GetEntry(file);
            if (e is null) continue;
            var html = new HtmlDocument { OptionFixNestedTags = true };
            using (var s = e.Open()) html.Load(s, Encoding.UTF8);
            var body = html.DocumentNode.SelectSingleNode("//body") ?? html.DocumentNode;
            anchors.TryAdd(file, blocks.Count);
            Walk(body, file, blocks, anchors);
        }
        // Project Gutenberg's markers, where the licence is not in its own section.
        int stop = blocks.FindIndex(b => b.Text.Contains("END OF THE PROJECT GUTENBERG EBOOK", StringComparison.OrdinalIgnoreCase)
                                        || b.Text.Contains("END OF THIS PROJECT GUTENBERG EBOOK", StringComparison.OrdinalIgnoreCase));
        if (stop >= 0) blocks.RemoveRange(stop, blocks.Count - stop);
        int begin = blocks.FindIndex(b => b.Text.Contains("START OF THE PROJECT GUTENBERG EBOOK", StringComparison.OrdinalIgnoreCase)
                                          || b.Text.Contains("START OF THIS PROJECT GUTENBERG EBOOK", StringComparison.OrdinalIgnoreCase));
        if (begin >= 0)
        {
            blocks.RemoveRange(0, begin + 1);
            foreach (var k in anchors.Keys.ToList()) anchors[k] -= begin + 1;
        }
        if (blocks.Count == 0) throw new InvalidDataException("the book has no text");

        // Positions.
        var text = new StringBuilder();
        var starts = new int[blocks.Count];
        for (int i = 0; i < blocks.Count; i++)
        {
            if (i > 0) text.Append('\n');
            starts[i] = text.Length;
            text.Append(blocks[i].Text);
        }

        // Chapters from the table of contents, else one per spine document.
        var toc = ReadToc(zip, opf, manifest, spineEl);
        var marks = new List<(int Block, string Title)>();
        foreach (var (href, label) in toc)
        {
            if (anchors.TryGetValue(href, out int b) || anchors.TryGetValue(href.Split('#')[0], out b))
                if (b >= 0 && b < blocks.Count && !string.IsNullOrWhiteSpace(label) && !label.Contains("PROJECT GUTENBERG", StringComparison.OrdinalIgnoreCase)) marks.Add((b, label));
        }
        if (marks.Count == 0)
        {
            foreach (var file in spine)
                if (anchors.TryGetValue(file, out int b) && b >= 0 && b < blocks.Count) marks.Add((b, blocks[b].Text.Length < 80 ? blocks[b].Text : $"Part {marks.Count + 1}"));
        }
        // In reading order, one chapter per block (the first title wins), and the front matter before
        // the first entry as a chapter of its own.
        marks = marks.GroupBy(m => m.Block).Select(g => g.First()).OrderBy(m => m.Block).ToList();
        if (marks.Count == 0 || marks[0].Block > 0) marks.Insert(0, (0, "Beginning"));
        var chapters = new List<Chapter>();
        for (int i = 0; i < marks.Count; i++)
        {
            int start = starts[marks[i].Block];
            int end = i + 1 < marks.Count ? starts[marks[i + 1].Block] : text.Length;
            chapters.Add(new Chapter(i, ChapterTitle(Clean(marks[i].Title)), start, end));
        }
        var finalBlocks = new List<Block>(blocks.Count);
        int ch = 0;
        for (int i = 0; i < blocks.Count; i++)
        {
            while (ch + 1 < chapters.Count && starts[i] >= chapters[ch + 1].Start) ch++;
            finalBlocks.Add(new Block(blocks[i].Kind, blocks[i].Text, starts[i], ch));
        }
        string full = text.ToString();
        string id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full)))[..16].ToLowerInvariant();
        return new Book { Title = title, Author = author, Text = full, Blocks = finalBlocks, Chapters = chapters, Id = id };
    }

    /// <summary>Headings and paragraphs, depth first. A block element holding other blocks is not a
    /// block itself; its own loose text is.</summary>
    static void Walk(HtmlNode node, string file, List<(BlockKind, string)> blocks, Dictionary<string, int> anchors)
    {
        foreach (var child in node.ChildNodes)
        {
            if (child.NodeType != HtmlNodeType.Element) continue;
            string name = child.Name.ToLowerInvariant();
            if (Skipped.Contains(name)) continue;
            string id = child.GetAttributeValue("id", "");
            // Project Gutenberg's licence: not the book.
            if (id is "pg-header" or "pg-footer" || child.GetAttributeValue("class", "").Contains("pg-boilerplate")) continue;
            if (id.Length > 0) anchors.TryAdd($"{file}#{id}", blocks.Count);
            foreach (var a in child.Descendants().Where(d => d.NodeType == HtmlNodeType.Element))
            {
                string aid = a.GetAttributeValue("id", a.GetAttributeValue("name", ""));
                if (aid.Length > 0 && !HasBlockInside(child)) anchors.TryAdd($"{file}#{aid}", blocks.Count);
            }
            if (BlockTags.Contains(name) && !HasBlockInside(child))
            {
                string text = Clean(HtmlEntity.DeEntitize(InnerText(child)));
                if (text.Length > 0)
                    blocks.Add((name.Length == 2 && name[0] == 'h' && char.IsDigit(name[1]) ? BlockKind.Heading : BlockKind.Paragraph, text));
            }
            else
            {
                Walk(child, file, blocks, anchors);
            }
        }
    }

    [GeneratedRegex(@"\b(CHAPTER|Chapter|BOOK|Book|PART|Part|LETTER|Letter)\b.*$")]
    private static partial Regex ChapterWord();

    /// <summary>Some tables of contents put an illustration's caption before the chapter's name
    /// ("I hope Mr. Bingley will like it. CHAPTER II."): the name is from "Chapter" on.</summary>
    static string ChapterTitle(string label)
    {
        var m = ChapterWord().Match(label);
        return m.Success && m.Index > 0 ? m.Value : label;
    }

    static bool HasBlockInside(HtmlNode n) =>
        n.Descendants().Any(d => d.NodeType == HtmlNodeType.Element && BlockTags.Contains(d.Name.ToLowerInvariant()));

    /// <summary>Text with line breaks as spaces; ruby annotations and footnote markers kept as text.</summary>
    static string InnerText(HtmlNode n)
    {
        var sb = new StringBuilder();
        foreach (var d in n.DescendantsAndSelf())
        {
            if (d.NodeType == HtmlNodeType.Text && !Skipped.Contains(d.ParentNode?.Name.ToLowerInvariant() ?? "")) sb.Append(d.InnerText);
            else if (d.Name == "br") sb.Append(' ');
        }
        return sb.ToString();
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    static string Clean(string s) => Spaces().Replace(s.Replace(' ', ' '), " ").Trim();

    static List<(string Href, string Label)> ReadToc(ZipArchive zip, XDocument opf, Dictionary<string, (string Href, string Type, string Props)> manifest, XElement spine)
    {
        var list = new List<(string, string)>();
        // EPUB 3: the navigation document's toc nav.
        var nav = manifest.Values.FirstOrDefault(m => m.Props.Split(' ').Contains("nav"));
        if (nav.Href is not null && zip.GetEntry(nav.Href) is { } navEntry)
        {
            var html = new HtmlDocument();
            using (var s = navEntry.Open()) html.Load(s, Encoding.UTF8);
            var tocNav = html.DocumentNode.Descendants("nav").FirstOrDefault(n => n.GetAttributeValue("epub:type", "").Contains("toc"))
                ?? html.DocumentNode.Descendants("nav").FirstOrDefault();
            if (tocNav is not null)
            {
                string dir = DirOf(nav.Href);
                foreach (var a in tocNav.Descendants("a"))
                {
                    string href = a.GetAttributeValue("href", "");
                    if (href.Length == 0) continue;
                    list.Add((Resolve(dir, href), HtmlEntity.DeEntitize(a.InnerText)));
                }
                if (list.Count > 0) return list;
            }
        }
        // EPUB 2: the NCX named by the spine.
        string? ncxId = spine.Attribute("toc")?.Value;
        var ncx = ncxId is not null && manifest.TryGetValue(ncxId, out var m) ? m.Href : manifest.Values.FirstOrDefault(x => x.Type == "application/x-dtbncx+xml").Href;
        if (ncx is not null && zip.GetEntry(ncx) is { } ncxEntry)
        {
            var doc = XDocument.Load(ncxEntry.Open());
            string dir = DirOf(ncx);
            foreach (var p in doc.Descendants(Ncx + "navPoint"))
            {
                string? src = p.Element(Ncx + "content")?.Attribute("src")?.Value;
                string label = p.Element(Ncx + "navLabel")?.Element(Ncx + "text")?.Value ?? "";
                if (src is not null) list.Add((Resolve(dir, src), label));
            }
        }
        return list;
    }

    static Stream Entry(ZipArchive zip, string name) =>
        (zip.GetEntry(name) ?? throw new InvalidDataException($"{name} is missing: not an EPUB")).Open();

    static string DirOf(string path)
    {
        int i = path.LastIndexOf('/');
        return i < 0 ? "" : path[..(i + 1)];
    }

    /// <summary>"dir/" + "../a/b.xhtml" -> "a/b.xhtml" (zip paths use forward slashes).</summary>
    static string Combine(string dir, string rel)
    {
        var parts = new List<string>();
        foreach (var p in (dir + rel).Split('/'))
        {
            if (p == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
            else if (p != "." && p.Length > 0) parts.Add(p);
        }
        return string.Join('/', parts);
    }

    static string Resolve(string dir, string href)
    {
        href = Uri.UnescapeDataString(href);
        int hash = href.IndexOf('#');
        string file = hash < 0 ? href : href[..hash];
        string frag = hash < 0 ? "" : href[(hash + 1)..];
        string full = Combine(dir, file);
        return frag.Length > 0 ? $"{full}#{frag}" : full;
    }
}
