using OH_Ross.Models;
using OH_Ross.Services;
using OH_Ross.Utils;

var input = ApifyHelper.GetInput<InputConfig>();
Console.WriteLine("Launching OH-Ross Court scraper with input config...");

var captchaService = new TwoCaptchaRecaptchaService(input.TwoCaptchaApiKey);
var scraper = new OhRossScraperService(captchaService);
await scraper.RunAsync(input);

Console.WriteLine("Done.");
Console.WriteLine("Press Enter to exit...");
Console.ReadLine();
