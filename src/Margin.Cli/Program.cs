using System.Text.Json;
using Margin.Core;

// margin-cli: look inside a book, or export its text with positions for the companion service.
//   margin-cli info book.epub
//   margin-cli export book.epub book.json
if (args.Length < 2)
{
    Console.Error.WriteLine("usage: margin-cli info <book.epub> | export <book.epub> <out.json>");
    return 2;
}
var book = EpubReader.Read(args[1]);
switch (args[0])
{
    case "info":
        Console.WriteLine($"{book.Title} - {book.Author} ({book.Id}): {book.Length:N0} characters, {book.Blocks.Count:N0} blocks");
        foreach (var c in book.Chapters)
            Console.WriteLine($"  {c.Index,3} {c.Start,8:N0} {c.End - c.Start,8:N0}  {c.Title}");
        return 0;
    case "export" when args.Length >= 3:
        File.WriteAllText(args[2], JsonSerializer.Serialize(BookJson.From(book), BookJson.Options));
        Console.WriteLine($"wrote {args[2]}");
        return 0;
    default:
        Console.Error.WriteLine($"unknown command {args[0]}");
        return 2;
}
