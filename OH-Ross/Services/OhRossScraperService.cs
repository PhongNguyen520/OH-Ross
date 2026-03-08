using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using OH_Ross.Models;
using OH_Ross.Utils;

namespace OH_Ross.Services;

/// <summary>Scraper service for Ross County (Ohio) Court records.</summary>
public class OhRossScraperService
{
    const string TargetUrl = "https://eaccess.co.ross.oh.us/eservices/home.page.2";
    const int MaxRetries = 3;
    const string DateFormat = "MM/dd/yyyy";
    static readonly string[] ShortFlowTypes = { "CHANGE OF NAME", "NAME CONFORMITY", "CHILD SUPPORT", "POWER OF ATTORNEY", "CARETAKER AUTHORIZATION" };

    readonly ICaptchaService _captchaService;

    public OhRossScraperService(ICaptchaService captchaService)
    {
        _captchaService = captchaService ?? throw new ArgumentNullException(nameof(captchaService));
    }

    /// <summary>Runs the full scrape workflow and reports final stats to Apify. Returns false if validation or case-type check failed (caller should exit with non-zero code).</summary>
    public async Task<bool> RunAsync(InputConfig input)
    {
        input ??= new InputConfig();
        var validationError = ValidateInput(input);
        if (validationError != null)
        {
            await ApifyHelper.SetStatusMessageAsync(validationError, isTerminal: true);
            return false;
        }

        var stats = new ScrapeRunStats();
        await ApifyHelper.SetStatusMessageAsync("Starting OH-Ross Court scraper...");

        IPlaywright? playwright = null;
        IBrowser? browser = null;
        IBrowserContext? context = null;
        IPage? page = null;

        try
        {
            playwright = await Playwright.CreateAsync();
            var isApify = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("APIFY_CONTAINER_PORT"));
            browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = isApify,
                Args = new[] { "--no-sandbox", "--disable-dev-shm-usage", "--disable-gpu" }
            });
            context = await browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
            page = await context.NewPageAsync();
            page.SetDefaultTimeout(60_000);

            await NavigateAndBypassCaptchaAsync(page);
            await SearchByDateAndCaseTypeAsync(page, input);
            await ProcessSearchResultsAsync(page, stats);
        }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("Invalid Case Types", StringComparison.Ordinal))
        {
            await ApifyHelper.SetStatusMessageAsync(ex.Message, isTerminal: true);
            return false;
        }
        finally
        {
            if (page != null) await page.CloseAsync();
            if (context != null) await context.CloseAsync();
            if (browser != null) await browser.CloseAsync();
            playwright?.Dispose();
        }

        var summary = $"Finished: Total {stats.Total}, Succeeded {stats.Succeeded}, Failed {stats.Failed}.";
        if (stats.FailedCases.Count > 0)
            summary += " Failed Cases: [" + string.Join(", ", stats.FailedCases) + "]";
        await ApifyHelper.SetStatusMessageAsync(summary, isTerminal: true);
        return true;
    }

    /// <summary>Validates StartDate and EndDate format (MM/DD/YYYY). Returns error message or null if valid.</summary>
    static string? ValidateInput(InputConfig input)
    {
        if (string.IsNullOrWhiteSpace(input.StartDate))
            return "Invalid input: StartDate is required (format MM/DD/YYYY).";
        if (string.IsNullOrWhiteSpace(input.EndDate))
            return "Invalid input: EndDate is required (format MM/DD/YYYY).";
        if (!TryParseDate(input.StartDate, out _))
            return "Invalid input: StartDate must be MM/DD/YYYY (e.g. 01/01/2024).";
        if (!TryParseDate(input.EndDate, out _))
            return "Invalid input: EndDate must be MM/DD/YYYY (e.g. 12/31/2024).";
        return null;
    }

    static bool TryParseDate(string value, out DateTime result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        return DateTime.TryParseExact(value.Trim(), DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out result);
    }

    /// <summary>Navigates to the target URL, solves CAPTCHA if present, and enters the search portal.</summary>
    public async Task NavigateAndBypassCaptchaAsync(IPage page)
    {
        await page.GotoAsync(TargetUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        var recaptchaElement = await page.QuerySelectorAsync(".g-recaptcha");
        if (recaptchaElement != null)
        {
            var siteKey = await recaptchaElement.GetAttributeAsync("data-sitekey");
            if (string.IsNullOrEmpty(siteKey))
                throw new InvalidOperationException("CAPTCHA element has no data-sitekey.");
            string? token = null;
            for (var attempt = 1; attempt <= MaxRetries && string.IsNullOrEmpty(token); attempt++)
            {
                token = await _captchaService.SolveRecaptchaV2Async(siteKey, TargetUrl);
                if (string.IsNullOrEmpty(token) && attempt < MaxRetries)
                    await Task.Delay(1000 * attempt);
            }
            if (string.IsNullOrEmpty(token))
                throw new InvalidOperationException("Captcha solver failed to obtain a token.");
            var callbackName = await recaptchaElement.GetAttributeAsync("data-callback");
            await page.EvaluateAsync(@"([token, callbackName]) => {
                var el = document.getElementById('g-recaptcha-response');
                if (el) el.value = token;
                if (callbackName && typeof window[callbackName] === 'function') window[callbackName](token);
            }", new object[] { token, callbackName ?? "" });
            await Task.Delay(1000);
        }
        await page.GetByText("Click Here").ClickAsync();
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
    }

    /// <summary>Fills the Case Type tab form and submits search; validates CaseTypes against page options; retries form fill when feedback panel errors appear.</summary>
    public async Task SearchByDateAndCaseTypeAsync(IPage page, InputConfig input)
    {
        var caseTypeTab = page.Locator("span:has-text('Case Type')");
        await caseTypeTab.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
        await caseTypeTab.ClickAsync();
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await page.WaitForSelectorAsync("select[name='caseCd']", new PageWaitForSelectorOptions { State = WaitForSelectorState.Visible, Timeout = 15000 });
        await Task.Delay(500);

        var validLabels = await GetCaseTypeOptionLabelsAsync(page);
        var invalidCaseTypes = GetInvalidCaseTypes(input.CaseTypes, validLabels);
        if (invalidCaseTypes.Count > 0)
        {
            var msg = "Invalid Case Types (not found on search page): [" + string.Join(", ", invalidCaseTypes) + "]. Stopping.";
            await ApifyHelper.SetStatusMessageAsync(msg, isTerminal: true);
            throw new InvalidOperationException(msg);
        }

        var searchSucceeded = false;
        for (var attempt = 1; attempt <= MaxRetries && !searchSucceeded; attempt++)
        {
            await FillSearchFormAndSubmitAsync(page, input);
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await Task.Delay(1500);

            var hasFeedbackError = await page.Locator("ul.feedbackPanel li.feedbackPanelERROR").CountAsync() > 0;
            if (!hasFeedbackError)
                searchSucceeded = true;
        }

        if (!searchSucceeded)
            throw new InvalidOperationException("Search form validation failed after " + MaxRetries + " attempts (feedback panel errors).");

        for (var attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                await page.WaitForSelectorAsync("table#grid.tableResults", new PageWaitForSelectorOptions { State = WaitForSelectorState.Visible, Timeout = 30_000 });
                break;
            }
            catch
            {
                if (attempt == MaxRetries) throw;
                await Task.Delay(2000);
            }
        }
    }

    /// <summary>Returns option labels from the Case Type select (excluding empty placeholder).</summary>
    static async Task<List<string>> GetCaseTypeOptionLabelsAsync(IPage page)
    {
        var options = await page.Locator("select[name='caseCd'] option").AllAsync();
        var labels = new List<string>();
        foreach (var opt in options)
        {
            var text = (await opt.InnerTextAsync())?.Trim() ?? "";
            if (string.IsNullOrEmpty(text)) continue;
            labels.Add(text);
        }
        return labels;
    }

    /// <summary>Returns input case types that are not present in the page's option labels (case-insensitive).</summary>
    static List<string> GetInvalidCaseTypes(string[]? inputCaseTypes, List<string> validLabels)
    {
        var invalid = new List<string>();
        if (inputCaseTypes == null || inputCaseTypes.Length == 0) return invalid;
        var validSet = validLabels.Select(l => l.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var ct in inputCaseTypes)
        {
            var t = (ct ?? "").Trim();
            if (string.IsNullOrEmpty(t)) continue;
            if (!validSet.Contains(t))
                invalid.Add(t);
        }
        return invalid;
    }

    /// <summary>Fills page size, case types, begin/end dates and clicks Search.</summary>
    static async Task FillSearchFormAndSubmitAsync(IPage page, InputConfig input)
    {
        await page.EvaluateAsync(@"() => {
            var select = document.querySelector('select[name=""topSearchPanel:pageSize""]');
            if (select) { select.value = '2'; select.onchange ? select.onchange() : select.dispatchEvent(new Event('change')); }
        }");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Task.Delay(1000);

        if (input.CaseTypes != null && input.CaseTypes.Length > 0)
        {
            var caseTypeSelect = page.Locator("select[name='caseCd']");
            await caseTypeSelect.SelectOptionAsync(input.CaseTypes.Select(ct => new SelectOptionValue { Label = ct }).ToArray());
            await page.EvaluateAsync(@"() => {
                var select = document.querySelector('select[name=""caseCd""]');
                if (select) { select.onchange ? select.onchange() : select.dispatchEvent(new Event('change')); }
            }");
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await Task.Delay(1000);
        }

        var beginDateInput = page.Locator("input[name='fileDateRange:dateInputBegin']");
        await beginDateInput.ClearAsync();
        await beginDateInput.PressSequentiallyAsync(input.StartDate, new LocatorPressSequentiallyOptions { Delay = 50 });
        await beginDateInput.PressAsync("Tab");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Task.Delay(1000);

        var endDateInput = page.Locator("input[name='fileDateRange:dateInputEnd']");
        await endDateInput.ClearAsync();
        await endDateInput.PressSequentiallyAsync(input.EndDate, new LocatorPressSequentiallyOptions { Delay = 50 });
        await endDateInput.PressAsync("Tab");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Task.Delay(1000);

        if (input.CaseTypes != null && input.CaseTypes.Length > 0)
        {
            var caseTypeSelect = page.Locator("select[name='caseCd']");
            await caseTypeSelect.SelectOptionAsync(input.CaseTypes.Select(ct => new SelectOptionValue { Label = ct }).ToArray());
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await Task.Delay(500);
        }

        var searchButton = page.Locator("input[name='submitLink']");
        await searchButton.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
        await searchButton.ClickAsync();
    }

    /// <summary>Iterates search result pages and rows; opens each standard-flow case in a new tab and extracts data.</summary>
    public async Task ProcessSearchResultsAsync(IPage page, ScrapeRunStats stats)
    {
        var processedCases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hasNextPage = true;

        while (hasNextPage)
        {
            await page.WaitForSelectorAsync("table#grid.tableResults tbody tr[class^='row']", new PageWaitForSelectorOptions { State = WaitForSelectorState.Visible, Timeout = 15000 });
            var rows = await page.Locator("table#grid tbody tr[class^='row']").AllAsync();

            for (var i = 0; i < rows.Count; i++)
            {
                var currentRow = page.Locator("table#grid tbody tr[class^='row']").Nth(i);
                var caseNumber = (await currentRow.Locator("td:nth-child(5)").InnerTextAsync())?.Trim() ?? "";
                if (string.IsNullOrEmpty(caseNumber)) continue;
                if (processedCases.Contains(caseNumber)) continue;

                var fileDate = (await currentRow.Locator("td:nth-child(6)").InnerTextAsync())?.Trim() ?? "";
                var initiatingAction = ((await currentRow.Locator("td:nth-child(7)").InnerTextAsync())?.Trim() ?? "").ToUpperInvariant();
                var caseStatus = (await currentRow.Locator("td:nth-child(8)").InnerTextAsync())?.Trim() ?? "";
                var isShortFlow = ShortFlowTypes.Any(t => initiatingAction.Contains(t));

                if (isShortFlow)
                    continue;

                Console.WriteLine($"Processing row {i + 1}...");
                stats.Total++;
                var href = await currentRow.Locator("td:nth-child(5) a").GetAttributeAsync("href");
                if (string.IsNullOrEmpty(href)) continue;

                var fullUrl = new Uri(new Uri(page.Url), href).ToString();
                IPage? detailsPage = null;
                try
                {
                    detailsPage = await page.Context.NewPageAsync();
                    await detailsPage.GotoAsync(fullUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
                    await ExtractStandardCaseAsync(detailsPage, caseNumber, fileDate, initiatingAction, caseStatus);
                    stats.Succeeded++;
                }
                catch (Exception)
                {
                    stats.Failed++;
                    stats.FailedCases.Add(caseNumber);
                    var msg = $"Processing: Total {stats.Total}, Succeeded {stats.Succeeded}, Failed {stats.Failed}. Failed Cases: [{string.Join(", ", stats.FailedCases)}]";
                    await ApifyHelper.SetStatusMessageAsync(msg);
                }
                finally
                {
                    if (detailsPage != null) await detailsPage.CloseAsync();
                }
                processedCases.Add(caseNumber);
            }

            var nextButton = page.Locator("a[title='Go to next page']");
            if (await nextButton.CountAsync() > 0 && await nextButton.IsVisibleAsync())
            {
                await nextButton.ClickAsync();
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                await Task.Delay(1500);
            }
            else
                hasNextPage = false;
        }
    }

    /// <summary>Extracts master and party data from the case details page and pushes one record to the dataset.</summary>
    async Task ExtractStandardCaseAsync(IPage detailsPage, string caseNumber, string fileDate, string initiatingAction, string caseStatus)
    {
        var record = new OhRossRecord
        {
            CaseNumber = caseNumber,
            FileDate = fileDate,
            InitiatingAction = initiatingAction,
            CaseStatus = caseStatus
        };

        for (var attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                await detailsPage.WaitForSelectorAsync("#caseHeader", new PageWaitForSelectorOptions { State = WaitForSelectorState.Visible, Timeout = 10000 });
                break;
            }
            catch
            {
                if (attempt == MaxRetries) throw;
                await Task.Delay(1000);
            }
        }

        var caseTypeLocator = detailsPage.Locator("li.caseHdrLabel:has-text('Case Type:') + li.caseHdrInfo");
        if (await caseTypeLocator.CountAsync() > 0) record.CaseType = (await caseTypeLocator.InnerTextAsync())?.Trim() ?? "";
        var actionLocator = detailsPage.Locator("li.caseHdrLabel:has-text('Action:') + li.caseHdrInfo");
        if (await actionLocator.CountAsync() > 0) record.Action = (await actionLocator.InnerTextAsync())?.Trim() ?? "";

        var partyTab = detailsPage.GetByText("Party", new PageGetByTextOptions { Exact = true });
        if (await partyTab.IsVisibleAsync())
        {
            await partyTab.ClickAsync();
            await detailsPage.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await Task.Delay(1500);
            for (var attempt = 1; attempt <= MaxRetries; attempt++)
            {
                try
                {
                    await detailsPage.WaitForSelectorAsync("div#ptyContainer", new PageWaitForSelectorOptions { State = WaitForSelectorState.Visible, Timeout = 10000 });
                    break;
                }
                catch
                {
                    if (attempt == MaxRetries) throw;
                    await Task.Delay(1000);
                }
            }
            var partyBlocks = await detailsPage.Locator("div#ptyContainer > div.rowodd, div#ptyContainer > div.roweven").AllAsync();
            foreach (var block in partyBlocks)
                record.Parties.AddRange(await ParsePartyBlockAsync(block));
        }

        await ApifyHelper.PushSingleDataAsync(record);
    }

    /// <summary>Parses one party block (primary party plus aliases) into a list of party records.</summary>
    static async Task<List<PartyRecord>> ParsePartyBlockAsync(ILocator block)
    {
        var list = new List<PartyRecord>();
        var name = (await block.Locator("div.ptyInfoLabel").InnerTextAsync())?.Trim() ?? "";
        var pTypeRaw = await block.Locator("div.ptyType").InnerTextAsync();
        var pType = (pTypeRaw?.Replace("-", "")?.Trim() ?? "").Trim();
        var dispDate = "";
        var dispDateLoc = block.Locator("li.ptyPersLabel:has-text('Disp Date') + li.ptyPersInfo");
        if (await dispDateLoc.CountAsync() > 0) dispDate = (await dispDateLoc.InnerTextAsync())?.Trim() ?? "";
        var disposition = "";
        var dispLoc = block.Locator("li.ptyPersLabel:has-text('Disposition') + li.ptyPersInfo");
        if (await dispLoc.CountAsync() > 0) disposition = (await dispLoc.InnerTextAsync())?.Trim() ?? "";
        var address = "";
        var addrLoc = block.Locator("div.ptyContact li.ptyContactInfo");
        if (await addrLoc.CountAsync() > 0)
            address = Regex.Replace(await addrLoc.InnerTextAsync() ?? "", @"\s+", " ").Trim();
        list.Add(new PartyRecord { Name = name, PartyType = pType, Address = address, DispDate = dispDate, Disposition = disposition });

        var aliasUls = await block.Locator("div.ptyAffl div.displayData ul").AllAsync();
        foreach (var aliasUl in aliasUls)
        {
            var aliasNameRaw = await aliasUl.Locator("li.ptyAfflName").InnerTextAsync();
            if (string.IsNullOrWhiteSpace(aliasNameRaw)) continue;
            var cleanAlias = Regex.Replace(aliasNameRaw, @"\b(AKA|FKA|NKA|DBA)\b", "", RegexOptions.IgnoreCase).Trim();
            cleanAlias = Regex.Replace(cleanAlias, @"\s+", " ").Trim();
            if (!string.IsNullOrEmpty(cleanAlias))
                list.Add(new PartyRecord { Name = cleanAlias, PartyType = pType, Address = address, DispDate = dispDate, Disposition = disposition });
        }
        return list;
    }
}
