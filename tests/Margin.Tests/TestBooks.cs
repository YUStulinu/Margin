using System.IO.Compression;
using System.Text;

namespace Margin.Tests;

/// <summary>Books for the tests: a small EPUB built here, and the Project Gutenberg ones in data/books.</summary>
static class TestBooks
{
    public static string Root
    {
        get
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !File.Exists(Path.Combine(d.FullName, "Margin.slnx")) && !File.Exists(Path.Combine(d.FullName, "Margin.sln"))) d = d.Parent;
            return d?.FullName ?? throw new DirectoryNotFoundException("Margin.sln not found");
        }
    }

    public static IEnumerable<string> Gutenberg => Directory.GetFiles(Path.Combine(Root, "data", "books"), "*.epub").OrderBy(p => p);

    /// <summary>An EPUB 3 with a nav document, two chapters in one file and a third in another, an
    /// entity HTML knows but XML does not (&amp;nbsp;), nested blocks and a Gutenberg-style licence footer.</summary>
    public static MemoryStream Small(bool ncxOnly = false)
    {
        var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string text)
            {
                var e = z.CreateEntry(name);
                using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
                w.Write(text);
            }
            Add("mimetype", "application/epub+zip");
            Add("META-INF/container.xml", """
                <?xml version="1.0"?>
                <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
                  <rootfiles><rootfile full-path="OPS/package.opf" media-type="application/oebps-package+xml"/></rootfiles>
                </container>
                """);
            string navItem = ncxOnly ? "" : """<item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>""";
            Add("OPS/package.opf", $"""
                <?xml version="1.0"?>
                <package xmlns="http://www.idpf.org/2007/opf" version="3.0">
                  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:title>The Small Book</dc:title><dc:creator>A. Writer</dc:creator></metadata>
                  <manifest>
                    {navItem}
                    <item id="ncx" href="toc.ncx" media-type="application/x-dtbncx+xml"/>
                    <item id="a" href="text/one.xhtml" media-type="application/xhtml+xml"/>
                    <item id="b" href="text/two%20b.xhtml" media-type="application/xhtml+xml"/>
                  </manifest>
                  <spine toc="ncx"><itemref idref="a"/><itemref idref="b"/></spine>
                </package>
                """);
            Add("OPS/nav.xhtml", """
                <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops"><body>
                <nav epub:type="toc"><ol>
                  <li><a href="text/one.xhtml#c1">Chapter One</a></li>
                  <li><a href="text/one.xhtml#c2">Chapter Two</a></li>
                  <li><a href="text/two%20b.xhtml">Chapter Three</a></li>
                </ol></nav></body></html>
                """);
            Add("OPS/toc.ncx", """
                <ncx xmlns="http://www.daisy.org/z3986/2005/ncx/" version="2005-1"><navMap>
                  <navPoint id="p1"><navLabel><text>Chapter One</text></navLabel><content src="text/one.xhtml#c1"/></navPoint>
                  <navPoint id="p2"><navLabel><text>Chapter Two</text></navLabel><content src="text/one.xhtml#c2"/></navPoint>
                  <navPoint id="p3"><navLabel><text>Chapter Three</text></navLabel><content src="text/two%20b.xhtml"/></navPoint>
                </navMap></ncx>
                """);
            Add("OPS/text/one.xhtml", """
                <html><head><title>x</title><style>p{}</style></head><body>
                <section id="pg-header"><p>The Project Gutenberg eBook of The Small Book</p></section>
                <h1 id="c1">Chapter One</h1>
                <p>It was a&nbsp;dark   and stormy
                night.</p>
                <div><p>The rain fell.</p><p>The wind <i>howled</i>.</p></div>
                <h2 id="c2">Chapter Two</h2>
                <p>Morning came<br/>at last.</p>
                </body></html>
                """);
            Add("OPS/text/two b.xhtml", """
                <html><body><h2>Chapter Three</h2><p>And then it was over.</p>
                <section id="pg-footer"><p>End of the Project Gutenberg EBook.</p></section></body></html>
                """);
        }
        ms.Position = 0;
        return ms;
    }
}
