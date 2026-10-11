using System.Xml.Linq;

namespace MediaWikiInventory;

public static class MediaWikiModuleSubset
{
    public static IReadOnlyList<string> Write(string inputPath, string outputPath, IReadOnlySet<string> titles)
    {
        if (titles.Count == 0) throw new ArgumentException("At least one module title is required.", nameof(titles));
        var document = XDocument.Load(inputPath, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
        var pages = document.Root?.Elements().Where(value => value.Name.LocalName == "page").ToArray()
            ?? throw new MediaWikiImportFormatException("The export has no root page collection.");
        var selected = pages.Where(page => titles.Contains(page.Elements().FirstOrDefault(value => value.Name.LocalName == "title")?.Value.Trim() ?? string.Empty)).ToArray();
        var missing = titles.Where(title => selected.All(page => !string.Equals(page.Elements().FirstOrDefault(value => value.Name.LocalName == "title")?.Value.Trim(), title, StringComparison.OrdinalIgnoreCase))).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (missing.Length > 0) throw new InvalidDataException("Requested module titles were not found: " + string.Join(", ", missing));
        document.Root!.Elements().Where(value => value.Name.LocalName == "page").Remove();
        document.Root.Add(selected);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        document.Save(outputPath, SaveOptions.DisableFormatting);
        return selected.Select(page => page.Elements().First(value => value.Name.LocalName == "title").Value.Trim()).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
