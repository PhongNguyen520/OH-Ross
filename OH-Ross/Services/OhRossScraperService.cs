using System.Linq;
using Microsoft.Playwright;
using OH_Ross.Models;
using OH_Ross.Utils;

namespace OH_Ross.Services;

/// <summary>Scraper service for Ross County (Ohio) Court records.</summary>
public class OhRossScraperService
{
    const string TargetUrl = "https://eaccess.co.ross.oh.us/eservices/home.page.2";

    readonly ICaptchaService _captchaService;

    public OhRossScraperService(ICaptchaService captchaService)
    {
        _captchaService = captchaService ?? throw new ArgumentNullException(nameof(captchaService));
    }

    /// <summary>Main entry: runs full scrape workflow using input configuration.</summary>
    public async Task RunAsync(InputConfig input)
    {
        input ??= new InputConfig();
        await ApifyHelper.SetStatusMessageAsync("Starting OH-Ross Court scraper...");

        IPlaywright? playwright = null;
        IBrowser? browser = null;
        IBrowserContext? context = null;
        IPage? page = null;

        try
        {
            playwright = await Playwright.CreateAsync();
            var isApify = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("APIFY_CONTAINER_PORT"));
            var browserArgs = new[] { "--no-sandbox", "--disable-dev-shm-usage", "--disable-gpu" };

            browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = isApify,
                Args = browserArgs
            });
            context = await browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
            page = await context.NewPageAsync();
            page.SetDefaultTimeout(60_000);

            await NavigateAndBypassCaptchaAsync(page);
            await SearchByDateAndCaseTypeAsync(page, input);
            await ProcessSearchResultsAsync(page);
        }
        finally
        {
            if (page != null) await page.CloseAsync();
            if (context != null) await context.CloseAsync();
            if (browser != null) await browser.CloseAsync();
            playwright?.Dispose();
        }
    }

    /// <summary>Phase 1: Navigate to the home page, handle CAPTCHA if present, and click through to the search portal.</summary>
    public async Task NavigateAndBypassCaptchaAsync(IPage page)
    {
        Console.WriteLine($"Navigating to {TargetUrl}...");
        await page.GotoAsync(TargetUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        // 1. Check if CAPTCHA is present
        var recaptchaElement = await page.QuerySelectorAsync(".g-recaptcha");
        if (recaptchaElement != null)
        {
            Console.WriteLine("CAPTCHA detected. Extracting sitekey...");

            // 2. Extract SiteKey
            var siteKey = await recaptchaElement.GetAttributeAsync("data-sitekey");
            if (string.IsNullOrEmpty(siteKey))
            {
                throw new Exception("Could not find data-sitekey attribute on the CAPTCHA element.");
            }

            Console.WriteLine($"SiteKey found: {siteKey}. Sending to Captcha solver...");

            // 3. Solve CAPTCHA using the injected service
            var token = await _captchaService.SolveRecaptchaV2Async(siteKey, TargetUrl);
            if (string.IsNullOrEmpty(token))
            {
                throw new Exception("Captcha solver failed to obtain a token.");
            }

            Console.WriteLine("CAPTCHA solved successfully. Injecting token...");

            // 4. Extract callback name (if any) and inject the token
            var callbackName = await recaptchaElement.GetAttributeAsync("data-callback");
            await page.EvaluateAsync(@"([token, callbackName]) => {
                var el = document.getElementById('g-recaptcha-response');
                if (el) el.value = token;
                if (callbackName && typeof window[callbackName] === 'function') {
                    window[callbackName](token);
                }
            }", new object[] { token, callbackName ?? "" });

            await Task.Delay(1000);
        }
        else
        {
            Console.WriteLine("No CAPTCHA detected on the home page.");
        }

        // 5. Click the button to enter the search portal
        Console.WriteLine("Clicking 'Click Here' to enter search portal...");
        var enterButton = page.GetByText("Click Here");
        await enterButton.ClickAsync();

        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        Console.WriteLine("Successfully entered the search portal.");
    }

    /// <summary>Phase 2: Switch to Case Type tab, then search by date range and case types.</summary>
    public async Task SearchByDateAndCaseTypeAsync(IPage page, InputConfig input)
    {
        Console.WriteLine("Switching to 'Case Type' tab...");

        // 1. Safely click the Case Type tab
        var caseTypeTab = page.Locator("span:has-text('Case Type')");
        // Wait for the element to be explicitly visible and attached before clicking
        await caseTypeTab.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
        await caseTypeTab.ClickAsync();

        // 2. Wait for the Wicket Ajax to finish rendering the new form
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // CRITICAL: Wait for the specific select element of the Case Type tab to appear
        // This guarantees the Wicket re-render is complete before we interact with the form.
        await page.WaitForSelectorAsync("select[name='caseCd']", new PageWaitForSelectorOptions { State = WaitForSelectorState.Visible, Timeout = 15000 });
        Console.WriteLine("Successfully switched to 'Case Type' tab. Form is ready.");
        await Task.Delay(500); // Tiny buffer for event bindings to attach

        // 3. Set "Number of Results" to 100 (value="2")
        Console.WriteLine("Setting Number of Results to 100...");
        await page.EvaluateAsync(@"() => {
            const select = document.querySelector('select[name=""topSearchPanel:pageSize""]');
            if (select) {
                select.value = '2';
                if (typeof select.onchange === 'function') { select.onchange(); } 
                else { select.dispatchEvent(new Event('change')); }
            }
        }");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Task.Delay(1000);

        // 4. Select Case Types (match by label: option values in HTML have trailing spaces e.g. "CI                            ")
        Console.WriteLine("Selecting Case Types...");
        if (input.CaseTypes != null && input.CaseTypes.Length > 0)
        {
            var caseTypeSelect = page.Locator("select[name='caseCd']");
            var optionsByLabel = input.CaseTypes.Select(ct => new SelectOptionValue { Label = ct }).ToArray();
            await caseTypeSelect.SelectOptionAsync(optionsByLabel);
            await page.EvaluateAsync(@"() => {
                const select = document.querySelector('select[name=""caseCd""]');
                if (select) {
                    if (typeof select.onchange === 'function') { select.onchange(); } 
                    else { select.dispatchEvent(new Event('change')); }
                }
            }");
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await Task.Delay(1000);
        }

        // 5. Fill Begin Date
        Console.WriteLine($"Entering Begin Date: {input.StartDate}");
        var beginDateInput = page.Locator("input[name='fileDateRange:dateInputBegin']");
        await beginDateInput.ClearAsync();
        await beginDateInput.PressSequentiallyAsync(input.StartDate, new LocatorPressSequentiallyOptions { Delay = 50 });
        await beginDateInput.PressAsync("Tab");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Task.Delay(1000);

        // 6. Fill End Date
        Console.WriteLine($"Entering End Date: {input.EndDate}");
        var endDateInput = page.Locator("input[name='fileDateRange:dateInputEnd']");
        await endDateInput.ClearAsync();
        await endDateInput.PressSequentiallyAsync(input.EndDate, new LocatorPressSequentiallyOptions { Delay = 50 });
        await endDateInput.PressAsync("Tab");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Task.Delay(1000);

        // 7. Re-select Case Types (filling dates may trigger Wicket Ajax that re-renders and clears the dropdown)
        if (input.CaseTypes != null && input.CaseTypes.Length > 0)
        {
            Console.WriteLine("Re-selecting Case Types after date entry...");
            var caseTypeSelect = page.Locator("select[name='caseCd']");
            var optionsByLabel = input.CaseTypes.Select(ct => new SelectOptionValue { Label = ct }).ToArray();
            await caseTypeSelect.SelectOptionAsync(optionsByLabel);
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await Task.Delay(500);
        }

        // 8. Click Search
        Console.WriteLine("Clicking Search...");
        var searchButton = page.Locator("input[name='submitLink']");
        // Ensure the button is enabled and visible before clicking
        await searchButton.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
        await searchButton.ClickAsync();

        Console.WriteLine("Waiting for results table to load...");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await page.WaitForSelectorAsync("table#grid.tableResults", new PageWaitForSelectorOptions { State = WaitForSelectorState.Visible, Timeout = 30_000 });
        Console.WriteLine("Search submitted successfully and results table is visible.");
    }

    /// <summary>Phase 3: Main data extraction loop and pagination over search results.</summary>
    public async Task ProcessSearchResultsAsync(IPage page)
    {
        var processedCases = new HashSet<string>();
        bool hasNextPage = true;

        while (hasNextPage)
        {
            Console.WriteLine("Scraping current page...");
            await page.WaitForSelectorAsync("table#grid.tableResults tbody tr[class^='row']", new PageWaitForSelectorOptions { State = WaitForSelectorState.Visible, Timeout = 15000 });
            var rows = await page.Locator("table#grid tbody tr[class^='row']").AllAsync();

            for (int i = 0; i < rows.Count; i++)
            {
                // We re-locate the rows inside the loop to avoid StaleElementReferenceException
                var currentRow = page.Locator("table#grid tbody tr[class^='row']").Nth(i);

                // Extract what is available on the SEARCH RESULTS table first
                var partyCompany = await currentRow.Locator("td:nth-child(3)").InnerTextAsync();
                var partyType = await currentRow.Locator("td:nth-child(4)").InnerTextAsync();
                var caseNumber = await currentRow.Locator("td:nth-child(5)").InnerTextAsync();
                var fileDate = await currentRow.Locator("td:nth-child(6)").InnerTextAsync();
                var initiatingAction = await currentRow.Locator("td:nth-child(7)").InnerTextAsync();
                var caseStatus = await currentRow.Locator("td:nth-child(8)").InnerTextAsync();

                // Clean up
                partyCompany = partyCompany?.Trim() ?? "";
                partyType = partyType?.Trim() ?? "";
                caseNumber = caseNumber?.Trim() ?? "";
                fileDate = fileDate?.Trim() ?? "";
                initiatingAction = initiatingAction?.Trim().ToUpper() ?? "";
                caseStatus = caseStatus?.Trim() ?? "";

                if (string.IsNullOrEmpty(caseNumber)) continue;

                if (processedCases.Contains(caseNumber))
                {
                    Console.WriteLine($"[SKIP] Case {caseNumber} already processed.");
                    continue;
                }

                string[] shortFlowTypes = { "CHANGE OF NAME", "NAME CONFORMITY", "CHILD SUPPORT", "POWER OF ATTORNEY", "CARETAKER AUTHORIZATION" };
                bool isShortFlow = shortFlowTypes.Any(t => initiatingAction.Contains(t));

                if (isShortFlow)
                {
                    Console.WriteLine($"-> Rerouting Case {caseNumber} to SHORT FLOW.");
                    // await ExtractShortCaseAsync(currentRow, caseNumber);
                }
                else
                {
                    Console.WriteLine($"-> Rerouting Case {caseNumber} to STANDARD FLOW. (Opening in new tab...)");
                    var caseLink = currentRow.Locator("td:nth-child(5) a");
                    var href = await caseLink.GetAttributeAsync("href");

                    if (!string.IsNullOrEmpty(href))
                    {
                        var baseUri = new Uri(page.Url);
                        var fullUrl = new Uri(baseUri, href).ToString();
                        var detailsPage = await page.Context.NewPageAsync();

                        try
                        {
                            await detailsPage.GotoAsync(fullUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

                            // PASS the pre-extracted data into the detail method (InitiatingAction from col 7)
                            await ExtractStandardCaseAsync(detailsPage, caseNumber, fileDate, initiatingAction, caseStatus);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[ERROR] Failed to process Case {caseNumber} in details page: {ex.Message}");
                        }
                        finally
                        {
                            await detailsPage.CloseAsync();
                        }
                    }
                    processedCases.Add(caseNumber);
                }
            }

            var nextButton = page.Locator("a[title='Go to next page']");
            if (await nextButton.CountAsync() > 0 && await nextButton.IsVisibleAsync())
            {
                Console.WriteLine("Navigating to next page...");
                await nextButton.ClickAsync();
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                await Task.Delay(1500);
            }
            else
            {
                Console.WriteLine("No more pages left.");
                hasNextPage = false;
            }
        }
    }

    /// <summary>Extracts data for short-flow cases (e.g. Change of Name, Child Support). Implement later.</summary>
    private async Task ExtractShortCaseAsync(ILocator currentRow, string caseNumber)
    {
        await Task.CompletedTask;
        // TODO: Extract data from row without navigating to details
    }

    /// <summary>Extracts data for standard cases by navigating to details page in a new tab.</summary>
    private async Task ExtractStandardCaseAsync(IPage detailsPage, string caseNumber, string fileDate, string initiatingAction, string caseStatus)
    {
        Console.WriteLine($"   [Tab] Extracting details for {caseNumber}...");

        var record = new OhRossRecord
        {
            CaseNumber = caseNumber,
            FileDate = fileDate,                  // From search page col 6
            InitiatingAction = initiatingAction,  // From search page col 7 "Initiating Action"
            CaseStatus = caseStatus               // From search page col 8
        };

        // 1. Extract the remaining Master Data (Case Type and Action) from the inner #caseHeader
        try
        {
            await detailsPage.WaitForSelectorAsync("#caseHeader", new PageWaitForSelectorOptions { State = WaitForSelectorState.Visible, Timeout = 10000 });

            var caseTypeLocator = detailsPage.Locator("li.caseHdrLabel:has-text('Case Type:') + li.caseHdrInfo");
            if (await caseTypeLocator.CountAsync() > 0) record.CaseType = (await caseTypeLocator.InnerTextAsync())?.Trim() ?? "";

            var actionLocator = detailsPage.Locator("li.caseHdrLabel:has-text('Action:') + li.caseHdrInfo");
            if (await actionLocator.CountAsync() > 0) record.Action = (await actionLocator.InnerTextAsync())?.Trim() ?? "";
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   [Warning] Could not extract Master data from inner page: {ex.Message}");
        }

        // 2. Switch to the 'Party' tab (use exact text to avoid matching "Party Charge Information")
        var partyTab = detailsPage.GetByText("Party", new() { Exact = true });
        if (await partyTab.IsVisibleAsync())
        {
            await partyTab.ClickAsync();
            await detailsPage.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await Task.Delay(1500); // Allow Wicket to render party container

            await detailsPage.WaitForSelectorAsync("div#ptyContainer", new PageWaitForSelectorOptions { State = WaitForSelectorState.Visible, Timeout = 10000 });

            // 3. Loop through all party blocks
            var partyBlocks = await detailsPage.Locator("div#ptyContainer > div.rowodd, div#ptyContainer > div.roweven").AllAsync();

            foreach (var block in partyBlocks)
            {
                var name = (await block.Locator("div.ptyInfoLabel").InnerTextAsync())?.Trim() ?? "";

                var pTypeRaw = await block.Locator("div.ptyType").InnerTextAsync();
                var pType = pTypeRaw?.Replace("-", "").Trim() ?? "";

                var dispDate = "";
                var dispDateLocator = block.Locator("li.ptyPersLabel:has-text('Disp Date') + li.ptyPersInfo");
                if (await dispDateLocator.CountAsync() > 0) dispDate = (await dispDateLocator.InnerTextAsync())?.Trim() ?? "";

                var disposition = "";
                var dispLocator = block.Locator("li.ptyPersLabel:has-text('Disposition') + li.ptyPersInfo");
                if (await dispLocator.CountAsync() > 0) disposition = (await dispLocator.InnerTextAsync())?.Trim() ?? "";

                var address = "";
                var addrLocator = block.Locator("div.ptyContact li.ptyContactInfo");
                if (await addrLocator.CountAsync() > 0)
                {
                    var rawAddr = await addrLocator.InnerTextAsync();
                    address = System.Text.RegularExpressions.Regex.Replace(rawAddr ?? "", @"\s+", " ").Trim();
                }

                // Add primary party
                record.Parties.Add(new PartyRecord { Name = name, PartyType = pType, Address = address, DispDate = dispDate, Disposition = disposition });

                // 4. Handle Aliases (They appear under div.box.ptyAffl)
                var aliasLocators = await block.Locator("div.ptyAffl div.displayData ul").AllAsync();
                foreach (var aliasUl in aliasLocators)
                {
                    var aliasNameRaw = await aliasUl.Locator("li.ptyAfflName").InnerTextAsync();
                    if (!string.IsNullOrWhiteSpace(aliasNameRaw))
                    {
                        string cleanAlias = System.Text.RegularExpressions.Regex.Replace(aliasNameRaw, @"\b(AKA|FKA|NKA|DBA)\b", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
                        cleanAlias = System.Text.RegularExpressions.Regex.Replace(cleanAlias, @"\s+", " ").Trim();

                        if (!string.IsNullOrEmpty(cleanAlias))
                        {
                            record.Parties.Add(new PartyRecord { Name = cleanAlias, PartyType = pType, Address = address, DispDate = dispDate, Disposition = disposition });
                        }
                    }
                }
            }
        }

        // Log full extracted record before push in the requested format
        Console.WriteLine($"\n=== [Record Summary] Case: {record.CaseNumber} (Total Parties/Aliases: {record.Parties.Count}) ===");
        for (int idx = 0; idx < record.Parties.Count; idx++)
        {
            var party = record.Parties[idx];
            Console.WriteLine($"--- Party {idx + 1} ---");
            Console.WriteLine(string.Format("{0,-25} : {1}", "Case Number", record.CaseNumber));
            Console.WriteLine(string.Format("{0,-25} : {1}", "File Date", record.FileDate));
            Console.WriteLine(string.Format("{0,-25} : {1}", "Case Type", record.CaseType));
            Console.WriteLine(string.Format("{0,-25} : {1}", "Case Status", record.CaseStatus));
            Console.WriteLine(string.Format("{0,-25} : {1}", "Action", record.Action));
            Console.WriteLine(string.Format("{0,-25} : {1}", "Initiating Action", record.InitiatingAction));
            Console.WriteLine(string.Format("{0,-25} : {1}", "Name", party.Name));
            Console.WriteLine(string.Format("{0,-25} : {1}", "PartyType", party.PartyType));
            Console.WriteLine(string.Format("{0,-25} : {1}", "Address", party.Address));
            Console.WriteLine(string.Format("{0,-25} : {1}", "Disp Date", party.DispDate));
            Console.WriteLine(string.Format("{0,-25} : {1}", "Disposition", party.Disposition));
        }
        Console.WriteLine("=========================================================\n");

        await ApifyHelper.PushSingleDataAsync(record);
    }
}
