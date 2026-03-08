namespace OH_Ross.Models;

/// <summary>Holds counts and failed case identifiers for the current scrape run.</summary>
public class ScrapeRunStats
{
    public int Total { get; set; }
    public int Succeeded { get; set; }
    public int Failed { get; set; }
    public List<string> FailedCases { get; } = new();
}
