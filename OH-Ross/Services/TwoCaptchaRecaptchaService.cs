namespace OH_Ross.Services;

/// <summary>2Captcha-based implementation of reCAPTCHA v2 solving.</summary>
public class TwoCaptchaRecaptchaService : ICaptchaService
{
    readonly string _apiKey;

    public TwoCaptchaRecaptchaService(string apiKey)
    {
        _apiKey = apiKey ?? "";
    }

    public Task<string?> SolveRecaptchaV2Async(string siteKey, string pageUrl)
    {
        return TwoCaptchaService.SolveRecaptchaV2Async(siteKey, pageUrl, _apiKey);
    }
}
