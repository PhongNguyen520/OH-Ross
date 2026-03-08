namespace OH_Ross.Models;

/// <summary>
/// Input configuration loaded from JSON (local input.json or Apify input).
/// Controls the Ross County Court search (e.g. date range).
/// </summary>
public class InputConfig
{
    /// <summary>
    /// Start date for the search range. Format: MM/DD/YYYY (e.g. 01/01/2024).
    /// </summary>
    public string StartDate { get; set; } = "";

    /// <summary>
    /// End date for the search range. Format: MM/DD/YYYY (e.g. 12/31/2024).
    /// </summary>
    public string EndDate { get; set; } = "";

    public string[] CaseTypes { get; set; } = Array.Empty<string>();

    /// <summary>2Captcha API key for reCAPTCHA solving. Required when the site shows CAPTCHA.</summary>
    public string TwoCaptchaApiKey { get; set; } = "";
}
