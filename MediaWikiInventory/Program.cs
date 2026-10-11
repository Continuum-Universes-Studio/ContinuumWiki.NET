using System.Text.Json;
using System.Text.Encodings.Web;

var exitCode = 0;
try
{
    exitCode = MediaWikiInventory.PlannerCli.Run(args);
}
catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or MediaWikiInventory.MediaWikiImportFormatException or System.Xml.XmlException)
{
    Console.Error.WriteLine($"MediaWiki tool failed: {ex.Message}");
    exitCode = 1;
}
return exitCode;
