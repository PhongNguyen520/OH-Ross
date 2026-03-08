using System.Collections.Generic;

namespace OH_Ross.Models;

/// <summary>
/// Represents a single party involved in a court case.
/// </summary>
public class PartyRecord
{
    // Mapped from "Header" (Standard Flow) or "Party/Company" (Short Flow)
    public string Name { get; set; } = string.Empty;

    // Mapped from "Header" (Standard Flow) or "Party Type" (Short Flow)
    public string PartyType { get; set; } = string.Empty;

    // Mapped from "Address" (Standard Flow only, usually for Defendants)
    public string Address { get; set; } = string.Empty;

    // Mapped from "Disp Date" (Standard Flow only)
    public string DispDate { get; set; } = string.Empty;

    // Mapped from "Disposition" (Standard Flow only)
    public string Disposition { get; set; } = string.Empty;
}

/// <summary>
/// Represents a single scraped court case.
/// Pushed as a single JSON object to the Apify dataset.
/// </summary>
public class OhRossRecord
{
    // --- Single Values (Master Level) ---
    public string CaseNumber { get; set; } = string.Empty;
    public string FileDate { get; set; } = string.Empty;

    // Covers both 'Case Type:' and 'Case Type' labels
    public string CaseType { get; set; } = string.Empty;

    // Covers both 'Case Status:' and 'Case Status' labels
    public string CaseStatus { get; set; } = string.Empty;

    // From the 'Action:' label (Standard Flow)
    public string Action { get; set; } = string.Empty;

    // From the 'Initiating Action' label (Short Flow)
    public string InitiatingAction { get; set; } = string.Empty;

    // --- Multiple Values (Names Level - Stored as a list of Objects) ---
    public List<PartyRecord> Parties { get; set; } = new();
}
