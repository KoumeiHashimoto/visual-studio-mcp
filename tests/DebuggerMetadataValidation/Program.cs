using System.Globalization;
using System.Text.Json;
using VisualStudioMcpServer.Tools;

/// <summary>Exercises locale-independent frame metadata and explicit source unavailability.</summary>
internal static class Program
{
    /// <summary>Runs the metadata regression checks without requiring a debugger.</summary>
    private static void Main()
    {
        var cases = new (string? Display, string Expected)[]
        {
            ("C++", "cpp"), ("C#", "csharp"), ("  VISUAL BASIC ", "basic"),
            ("F#", "fsharp"), ("JavaScript", "javascript"), ("TypeScript", "typescript"),
            ("Python", "python"), ("SQL", "sql"), ("不明", "unknown"),
            ("Unknown", "unknown"), ("Unbekannt", "unknown"), (null, "unknown"),
            ("", "unknown"), ("unrecognized extension language", "unknown")
        };
        foreach (var cultureName in new[] { "en-US", "ja-JP", "tr-TR" })
        {
            CultureInfo.CurrentCulture = new CultureInfo(cultureName);
            foreach (var item in cases)
                if (DebuggerMetadata.NormalizeLanguage(item.Display) != item.Expected)
                    throw new InvalidOperationException("Language normalization depends on locale or an unsupported label.");
        }
        var frame = new DebuggerFrameInfo { Index = 1, Language = "unknown", LanguageDisplayName = "不明" };
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(frame));
        var root = json.RootElement;
        if (root.GetProperty("File").ValueKind != JsonValueKind.Null || root.GetProperty("Line").ValueKind != JsonValueKind.Null ||
            root.GetProperty("SourceLocationAvailable").GetBoolean() ||
            root.GetProperty("SourceLocationUnavailableReason").GetString() != "source_location_interface_unavailable" ||
            root.GetProperty("LanguageDisplayName").GetString() != "不明")
            throw new InvalidOperationException("Unavailable source metadata or original language label was lost during serialization.");
        frame.File = "sample.cpp";
        frame.Line = 12;
        frame.SourceLocationUnavailableReason = null;
        using var located = JsonDocument.Parse(JsonSerializer.Serialize(frame));
        if (!located.RootElement.GetProperty("SourceLocationAvailable").GetBoolean() ||
            located.RootElement.GetProperty("File").GetString() != "sample.cpp" ||
            located.RootElement.GetProperty("Line").GetInt32() != 12 ||
            located.RootElement.GetProperty("SourceLocationUnavailableReason").ValueKind != JsonValueKind.Null)
            throw new InvalidOperationException("Available source coordinates were lost during serialization.");
        Console.WriteLine($"Passed {cases.Length * 3 + 2} debugger metadata checks.");
    }
}
