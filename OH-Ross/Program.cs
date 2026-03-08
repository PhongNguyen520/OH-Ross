using OH_Ross.Models;
using OH_Ross.Services;
using OH_Ross.Utils;

var input = ApifyHelper.GetInput<InputConfig>();
Console.WriteLine("Launching OH-Ross Court scraper with input config...");

// Determine if running on Apify platform
bool isApify = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("APIFY_CONTAINER_PORT"));
string apiKey = string.Empty;

if (isApify)
{
    Console.WriteLine("[INFO] Running on Apify environment. Reading 2Captcha key from Environment Variables...");
    apiKey = Environment.GetEnvironmentVariable("TWO_CAPTCHA_API_KEY") ?? "";

    // Fallback to alternative env name just in case
    if (string.IsNullOrWhiteSpace(apiKey))
    {
        apiKey = Environment.GetEnvironmentVariable("TWO_CAPTCHA_KEY") ?? "";
    }
}
else
{
    Console.WriteLine("[INFO] Running locally. Reading 2Captcha key from input.json...");
    apiKey = input?.TwoCaptchaApiKey ?? "";

    // Safety fallback for local if input.json is empty
    if (string.IsNullOrWhiteSpace(apiKey))
    {
        apiKey = Environment.GetEnvironmentVariable("TWO_CAPTCHA_API_KEY") ?? "";
    }
}

apiKey = apiKey?.Trim() ?? "";

if (string.IsNullOrWhiteSpace(apiKey))
{
    Console.WriteLine("[WARN] 2Captcha API key not found. Ensure it is set in input.json (local) or Environment variables (Apify).");
}

var captchaService = new TwoCaptchaRecaptchaService(apiKey);
var scraper = new OhRossScraperService(captchaService);
await scraper.RunAsync(input);

Console.WriteLine("Done.");
Console.WriteLine("Press Enter to exit...");
Console.ReadLine();
