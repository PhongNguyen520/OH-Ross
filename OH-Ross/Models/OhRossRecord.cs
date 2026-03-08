using System.Collections.Generic;

namespace OH_Ross.Models;

/// <summary>Represents a single party involved in a court case.</summary>
public class PartyRecord
{
    public string Name { get; set; } = string.Empty;
    public string PartyType { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string DispDate { get; set; } = string.Empty;
    public string Disposition { get; set; } = string.Empty;
}

/// <summary>Represents a single scraped court case pushed as one item to the Apify dataset.</summary>
public class OhRossRecord
{
    public string CaseNumber { get; set; } = string.Empty;
    public string FileDate { get; set; } = string.Empty;
    public string CaseType { get; set; } = string.Empty;
    public string CaseStatus { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string InitiatingAction { get; set; } = string.Empty;
    public List<PartyRecord> Parties { get; set; } = new();
}
