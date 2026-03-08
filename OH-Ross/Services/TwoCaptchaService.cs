using System.Net.Http;
using Newtonsoft.Json.Linq;

namespace OH_Ross.Services;

/// <summary>
/// Helper for solving image captchas via 2Captcha API (Normal Image Captcha).
/// Uses HttpClient and Newtonsoft.Json only.
/// </summary>
public static class TwoCaptchaService
{
    const string InUrl = "https://2captcha.com/in.php";
    const string ResUrl = "https://2captcha.com/res.php";
    const int PollIntervalMs = 5000;
    const int MaxWaitMs = 60000;

    /// <summary>
    /// Solves a base64-encoded captcha image using 2Captcha API.
    /// </summary>
    /// <param name="base64Image">Base64-encoded image (without data URL prefix).</param>
    /// <param name="apiKey">Your 2Captcha API key.</param>
    /// <returns>The solved captcha text, or null on failure.</returns>
    public static async Task<string?> SolveImageCaptchaAsync(string base64Image, string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Console.WriteLine("[2Captcha] API key is empty.");
            return null;
        }
        if (string.IsNullOrWhiteSpace(base64Image))
        {
            Console.WriteLine("[2Captcha] Base64 image is empty.");
            return null;
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        // Step 1: Submit captcha
        Console.WriteLine("[2Captcha] Submitting captcha image...");
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["method"] = "base64",
            ["key"] = apiKey,
            ["body"] = base64Image,
            ["json"] = "1"
        });

        HttpResponseMessage? inResp = null;
        try
        {
            inResp = await http.PostAsync(InUrl, form);
            inResp.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[2Captcha] Submit error: {ex.Message}");
            return null;
        }

        var inJson = await inResp.Content.ReadAsStringAsync();
        var inObj = JObject.Parse(inJson);
        var status = inObj["status"]?.Value<int>() ?? 0;
        var requestId = inObj["request"]?.ToString();

        if (status != 1 || string.IsNullOrEmpty(requestId))
        {
            Console.WriteLine($"[2Captcha] Submit failed: {inJson}");
            return null;
        }

        Console.WriteLine($"[2Captcha] Task ID: {requestId}. Polling for result...");

        // Step 2: Poll for result
        var stopAt = DateTime.UtcNow.AddMilliseconds(MaxWaitMs);
        while (DateTime.UtcNow < stopAt)
        {
            await Task.Delay(PollIntervalMs);

            try
            {
                var resUrl = $"{ResUrl}?key={Uri.EscapeDataString(apiKey)}&action=get&id={Uri.EscapeDataString(requestId)}&json=1";
                var resResp = await http.GetAsync(resUrl);
                resResp.EnsureSuccessStatusCode();

                var resJson = await resResp.Content.ReadAsStringAsync();
                var resObj = JObject.Parse(resJson);
                var resStatus = resObj["status"]?.Value<int>() ?? 0;
                var resRequest = resObj["request"]?.ToString();

                if (resStatus == 1 && !string.IsNullOrEmpty(resRequest))
                {
                    Console.WriteLine($"[2Captcha] Solved: {resRequest}");
                    return resRequest;
                }

                if (string.Equals(resRequest, "CAPCHA_NOT_READY", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("[2Captcha] Not ready yet, waiting...");
                    continue;
                }

                Console.WriteLine($"[2Captcha] Result error: {resJson}");
                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[2Captcha] Poll error: {ex.Message}");
            }
        }

        Console.WriteLine("[2Captcha] Timeout waiting for result.");
        return null;
    }

    /// <summary>
    /// Solves reCAPTCHA v2 using 2Captcha API (method=userrecaptcha).
    /// </summary>
    /// <param name="siteKey">The data-sitekey from the reCAPTCHA widget.</param>
    /// <param name="pageUrl">Full URL of the page where the captcha is loaded.</param>
    /// <param name="apiKey">Your 2Captcha API key.</param>
    /// <returns>The solved reCAPTCHA token, or null on failure.</returns>
    public static async Task<string?> SolveRecaptchaV2Async(string siteKey, string pageUrl, string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Console.WriteLine("[2Captcha] API key is empty.");
            return null;
        }
        if (string.IsNullOrWhiteSpace(siteKey))
        {
            Console.WriteLine("[2Captcha] SiteKey is empty.");
            return null;
        }
        if (string.IsNullOrWhiteSpace(pageUrl))
        {
            Console.WriteLine("[2Captcha] PageUrl is empty.");
            return null;
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        Console.WriteLine("[2Captcha] Submitting reCAPTCHA v2 task...");
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["method"] = "userrecaptcha",
            ["key"] = apiKey,
            ["googlekey"] = siteKey,
            ["pageurl"] = pageUrl,
            ["json"] = "1"
        });

        HttpResponseMessage? inResp = null;
        try
        {
            inResp = await http.PostAsync(InUrl, form);
            inResp.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[2Captcha] Submit error: {ex.Message}");
            return null;
        }

        var inJson = await inResp.Content.ReadAsStringAsync();
        var inObj = JObject.Parse(inJson);
        var status = inObj["status"]?.Value<int>() ?? 0;
        var requestId = inObj["request"]?.ToString();

        if (status != 1 || string.IsNullOrEmpty(requestId))
        {
            Console.WriteLine($"[2Captcha] reCAPTCHA submit failed: {inJson}");
            return null;
        }

        Console.WriteLine($"[2Captcha] reCAPTCHA task ID: {requestId}. Polling for result...");

        var stopAt = DateTime.UtcNow.AddMilliseconds(120_000); // reCAPTCHA can take longer
        while (DateTime.UtcNow < stopAt)
        {
            await Task.Delay(PollIntervalMs);

            try
            {
                var resUrl = $"{ResUrl}?key={Uri.EscapeDataString(apiKey)}&action=get&id={Uri.EscapeDataString(requestId)}&json=1";
                var resResp = await http.GetAsync(resUrl);
                resResp.EnsureSuccessStatusCode();

                var resJson = await resResp.Content.ReadAsStringAsync();
                var resObj = JObject.Parse(resJson);
                var resStatus = resObj["status"]?.Value<int>() ?? 0;
                var resRequest = resObj["request"]?.ToString();

                if (resStatus == 1 && !string.IsNullOrEmpty(resRequest))
                {
                    Console.WriteLine("[2Captcha] reCAPTCHA solved successfully.");
                    return resRequest;
                }

                if (string.Equals(resRequest, "CAPCHA_NOT_READY", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("[2Captcha] reCAPTCHA not ready yet, waiting...");
                    continue;
                }

                Console.WriteLine($"[2Captcha] reCAPTCHA result error: {resJson}");
                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[2Captcha] reCAPTCHA poll error: {ex.Message}");
            }
        }

        Console.WriteLine("[2Captcha] Timeout waiting for reCAPTCHA result.");
        return null;
    }
}
