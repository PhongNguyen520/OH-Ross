using OH_Ross.Models;
using OH_Ross.Services;
using OH_Ross.Utils;

var input = ApifyHelper.GetInput<InputConfig>();
Console.WriteLine("Launching OH-Ross Court scraper with input config...");

// Resolve 2Captcha API key: from input first, then from env (for Apify secrets)
var apiKey = input?.TwoCaptchaApiKey?.Trim() ?? Environment.GetEnvironmentVariable("TWO_CAPTCHA_API_KEY")?.Trim() ?? "";
var captchaService = new TwoCaptchaRecaptchaService(apiKey);
var scraper = new OhRossScraperService(captchaService);
await scraper.RunAsync(input);

Console.WriteLine("Done.");
Console.WriteLine("Press Enter to exit...");
Console.ReadLine();
