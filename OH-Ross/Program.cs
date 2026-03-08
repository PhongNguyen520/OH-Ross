using OH_Ross.Models;
using OH_Ross.Services;
using OH_Ross.Utils;

var input = ApifyHelper.GetInput<InputConfig>();
Console.WriteLine("Launching OH-Ross Court scraper with input config...");

// Resolve 2Captcha API key: input first, then env vars (Apify: Actor Settings → Environment variables)
var apiKey = input?.TwoCaptchaApiKey?.Trim()
    ?? Environment.GetEnvironmentVariable("TWO_CAPTCHA_API_KEY")?.Trim()
    ?? Environment.GetEnvironmentVariable("TWO_CAPTCHA_KEY")?.Trim()
    ?? "";
if (string.IsNullOrWhiteSpace(apiKey))
    Console.WriteLine("[WARN] 2Captcha API key not found. Set 'twoCaptchaApiKey' in Run Input, or TWO_CAPTCHA_API_KEY in Actor Settings → Environment variables.");
var captchaService = new TwoCaptchaRecaptchaService(apiKey);
var scraper = new OhRossScraperService(captchaService);
await scraper.RunAsync(input);

Console.WriteLine("Done.");
Console.WriteLine("Press Enter to exit...");
Console.ReadLine();
