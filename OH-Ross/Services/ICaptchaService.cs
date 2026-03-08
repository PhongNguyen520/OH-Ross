namespace OH_Ross.Services;

/// <summary>Abstraction for solving reCAPTCHA v2 challenges.</summary>
public interface ICaptchaService
{
    /// <summary>Solves reCAPTCHA v2 and returns the token.</summary>
    Task<string?> SolveRecaptchaV2Async(string siteKey, string pageUrl);
}
