using System.Diagnostics;
using System.Text.Json.Nodes;
using BrowserTesting.Core.Abstractions;
using BrowserTesting.Core.Models;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Interactions;

namespace BrowserTesting.Infrastructure.Browser;

public sealed class BrowserSessionManager(AppSettings settings) : IBrowserSessionManager
{
    private readonly SemaphoreSlim sessionGate = new(1, 1);
    private readonly Dictionary<Guid, BrowserSessionSnapshot> snapshots = [];
    private readonly Dictionary<Guid, Dictionary<string, BrowserElementReference>> elementReferences = [];
    private BrowserSession? activeSession;

    public async Task<BrowserSessionSnapshot> OpenBrowserAsync(Guid testRunId, string? startUrl, string profilePath, bool headless, CancellationToken cancellationToken)
    {
        return await RunLockedAsync(() =>
        {
            CloseActiveSessionLocked();

            var session = CreateSession(testRunId, profilePath, headless);
            activeSession = session;

            if (!string.IsNullOrWhiteSpace(startUrl))
            {
                session.Driver.Navigate().GoToUrl(startUrl);
            }

            return CaptureAndCacheSnapshotLocked(session, RestoreStatus.Active);
        }, cancellationToken);
    }

    public async Task<BrowserSessionSnapshot?> GetSnapshotAsync(Guid testRunId, CancellationToken cancellationToken)
    {
        return await RunLockedAsync(() =>
        {
            if (activeSession?.TestRunId == testRunId)
            {
                if (!IsSessionAlive(activeSession))
                {
                    return MarkActiveSessionClosedLocked();
                }

                return CaptureAndCacheSnapshotLocked(activeSession, RestoreStatus.Active);
            }

            return snapshots.TryGetValue(testRunId, out var snapshot)
                ? CloneSnapshot(snapshot)
                : null;
        }, cancellationToken);
    }

    public async Task<ToolExecutionResult> ExecuteBrowserToolAsync(
        Guid testRunId,
        string toolName,
        JsonObject arguments,
        BrowserSessionSnapshot? persistedSnapshot,
        bool headless,
        CancellationToken cancellationToken)
    {
        if (toolName == "open_browser")
        {
            var profileName = GetString(arguments, "profile_name") ?? testRunId.ToString("N");
            var profilePath = Path.Combine(settings.ChromeProfileRoot, profileName);
            var startUrl = GetString(arguments, "url");
            return await RunLockedAsync(() =>
            {
                if (activeSession?.TestRunId == testRunId)
                {
                    if (!IsSessionAlive(activeSession))
                    {
                        MarkActiveSessionClosedLocked();
                    }
                    else
                    {
                        var driver = activeSession.Driver;
                        if (!string.IsNullOrWhiteSpace(startUrl) &&
                            !string.Equals(SafeGet(() => driver.Url), startUrl, StringComparison.OrdinalIgnoreCase))
                        {
                            driver.Navigate().GoToUrl(startUrl);
                            elementReferences.Remove(testRunId);
                        }

                        var currentSnapshot = CaptureAndCacheSnapshotLocked(activeSession, RestoreStatus.Active);
                        return ToolExecutionResult.Successful(
                            "Chrome already open.",
                            SnapshotNode(currentSnapshot),
                            "The active browser session was reused. Continue with page tools; do not call open_browser again unless the browser closes.");
                    }
                }

                CloseActiveSessionLocked();
                var session = CreateSession(testRunId, profilePath, headless);
                activeSession = session;
                elementReferences.Remove(testRunId);

                if (!string.IsNullOrWhiteSpace(startUrl))
                {
                    session.Driver.Navigate().GoToUrl(startUrl);
                }

                var snapshot = CaptureAndCacheSnapshotLocked(session, RestoreStatus.Active);
                return ToolExecutionResult.Successful("Chrome opened.", SnapshotNode(snapshot));
            }, cancellationToken);
        }

        if (toolName == "close_browser")
        {
            await CloseBrowserAsync(testRunId, cancellationToken);
            return ToolExecutionResult.Successful("Browser closed.");
        }

        return await RunLockedAsync(async () =>
        {
            if (activeSession?.TestRunId != testRunId)
            {
                return CreateNoActiveBrowserResult(persistedSnapshot);
            }

            if (!IsSessionAlive(activeSession))
            {
                MarkActiveSessionClosedLocked();
                return ToolExecutionResult.Failed("Browser window is closed.", "Use `open_browser` to launch a new session.");
            }

            var session = activeSession;
            var driver = session.Driver;

            try
            {
                var beforeUrl = SafeGet(() => driver.Url);
                var result = toolName switch
                {
                    "list_tabs" => ToolExecutionResult.Successful("Tabs listed.", SnapshotNode(CaptureAndCacheSnapshotLocked(session, RestoreStatus.Active))),
                    "switch_tab" => SwitchTab(driver, arguments),
                    "goto_url" => NavigateTo(driver, arguments),
                    "back" => Navigate(driver, d => d.Navigate().Back(), "Navigated back."),
                    "forward" => Navigate(driver, d => d.Navigate().Forward(), "Navigated forward."),
                    "refresh" => Navigate(driver, d => d.Navigate().Refresh(), "Page refreshed."),
                    "get_page_state" => ToolExecutionResult.Successful("Page state captured.", SnapshotNode(CaptureAndCacheSnapshotLocked(session, RestoreStatus.Active))),
                    "find_element" => FindElement(driver, arguments, many: false),
                    "find_elements" => FindElement(driver, arguments, many: true),
                    "inspect_page" => InspectPage(testRunId, driver, arguments),
                    "click_ref" => ClickRef(testRunId, driver, arguments),
                    "type_ref" => TypeRef(testRunId, driver, arguments),
                    "click" => Interact(driver, arguments, element => element.Click(), "Element clicked."),
                    "double_click" => DoubleClick(driver, arguments),
                    "type_text" => TypeText(driver, arguments),
                    "clear" => Interact(driver, arguments, element => element.Clear(), "Element cleared."),
                    "send_keys" => SendKeys(driver, arguments),
                    "submit" => Interact(driver, arguments, element => element.Submit(), "Form submitted."),
                    "select_option" => SelectOption(driver, arguments),
                    "hover" => Hover(driver, arguments),
                    "scroll_into_view" => ScrollIntoView(driver, arguments),
                    "get_text" => ReadText(driver, arguments),
                    "get_attribute" => ReadAttribute(driver, arguments),
                    "get_html" => GetHtml(driver, arguments),
                    "take_screenshot" => TakeScreenshot(driver, arguments),
                    "execute_javascript" => ExecuteJavaScript(driver, arguments),
                    "get_cookies" => GetCookies(driver),
                    "set_cookie" => SetCookie(driver, arguments),
                    "read_local_storage" => ExecuteJavaScript(driver, new JsonObject
                    {
                        ["script"] = "return window.localStorage.getItem(arguments[0]);",
                        ["arguments"] = new JsonArray(GetString(arguments, "key") ?? string.Empty),
                    }),
                    "write_local_storage" => ExecuteJavaScript(driver, new JsonObject
                    {
                        ["script"] = "window.localStorage.setItem(arguments[0], arguments[1]); return true;",
                        ["arguments"] = new JsonArray(GetString(arguments, "key") ?? string.Empty, GetString(arguments, "value") ?? string.Empty),
                    }),
                    _ => await ExecuteAsyncTool(driver, toolName, arguments, cancellationToken),
                };

                return FinalizeBrowserToolResult(testRunId, toolName, beforeUrl, driver, result);
            }
            catch (Exception ex) when (IsClosedBrowserException(ex))
            {
                MarkActiveSessionClosedLocked();
                return ToolExecutionResult.Failed("Browser window is closed.", ex.Message);
            }
        }, cancellationToken);
    }

    public async Task CloseBrowserAsync(Guid testRunId, CancellationToken cancellationToken)
    {
        await RunLockedAsync(() =>
        {
            if (activeSession?.TestRunId == testRunId)
            {
                MarkActiveSessionClosedLocked();
                return;
            }

            if (snapshots.TryGetValue(testRunId, out var snapshot))
            {
                snapshots[testRunId] = CloseSnapshot(snapshot);
            }
        }, cancellationToken);
    }

    private BrowserSession CreateSession(Guid testRunId, string profilePath, bool headless)
    {
        Directory.CreateDirectory(settings.ChromeProfileRoot);
        Directory.CreateDirectory(settings.ScreenshotDirectory);
        Directory.CreateDirectory(profilePath);

        var options = new ChromeOptions();
        options.AddArgument("--disable-gpu");
        options.AddArgument($"--user-data-dir={profilePath}");
        if (headless)
        {
            options.AddArgument("--headless=new");
            options.AddArgument("--window-size=1920,1080");
        }
        else
        {
            options.AddArgument("--window-size=1920,1080");
            options.AddArgument("--no-first-run");
            options.AddArgument("--no-default-browser-check");
        }

        var service = ChromeDriverService.CreateDefaultService();
        service.HideCommandPromptWindow = true;
        var driver = new ChromeDriver(service, options);
        CloseExtraWindows(driver);

        return new BrowserSession(testRunId, profilePath, service, driver);
    }

    private static void CloseExtraWindows(ChromeDriver driver)
    {
        var primaryHandle = driver.CurrentWindowHandle;
        var extraHandles = driver.WindowHandles
            .Where(handle => !string.Equals(handle, primaryHandle, StringComparison.Ordinal))
            .ToArray();

        foreach (var handle in extraHandles)
        {
            try
            {
                driver.SwitchTo().Window(handle);
                driver.Close();
            }
            catch
            {
            }
        }

        if (driver.WindowHandles.Contains(primaryHandle))
        {
            driver.SwitchTo().Window(primaryHandle);
        }
    }

    private static ToolExecutionResult SwitchTab(IWebDriver driver, JsonObject arguments)
    {
        if (arguments["handle"] is JsonValue handleValue && handleValue.TryGetValue<string>(out var handle))
        {
            driver.SwitchTo().Window(handle);
            return ToolExecutionResult.Successful($"Switched to tab `{handle}`.");
        }

        if (arguments["index"] is JsonValue indexValue && indexValue.TryGetValue<int>(out var index))
        {
            var targetHandle = driver.WindowHandles[index];
            driver.SwitchTo().Window(targetHandle);
            return ToolExecutionResult.Successful($"Switched to tab {index}.", new JsonObject { ["handle"] = targetHandle });
        }

        return ToolExecutionResult.Failed("A tab handle or index is required.");
    }

    private static ToolExecutionResult NavigateTo(IWebDriver driver, JsonObject arguments)
    {
        var url = GetString(arguments, "url");
        if (string.IsNullOrWhiteSpace(url))
        {
            return ToolExecutionResult.Failed("A URL is required.");
        }

        driver.Navigate().GoToUrl(url);
        return ToolExecutionResult.Successful($"Navigated to {url}.");
    }

    private static ToolExecutionResult Navigate(IWebDriver driver, Action<IWebDriver> action, string summary)
    {
        action(driver);
        return ToolExecutionResult.Successful(summary);
    }

    private static ToolExecutionResult FindElement(IWebDriver driver, JsonObject arguments, bool many)
    {
        if (many)
        {
            var elements = driver.FindElements(GetBy(arguments));
            const int maxReturned = 40;
            return ToolExecutionResult.Successful(
                $"Found {elements.Count} elements.",
                new JsonObject
                {
                    ["count"] = elements.Count,
                    ["truncated"] = elements.Count > maxReturned,
                    ["elements"] = new JsonArray(elements.Take(maxReturned).Select(DescribeElement).ToArray()),
                },
                elements.Count > maxReturned ? $"Result capped at {maxReturned} elements. Use inspect_page for compact page refs." : null);
        }

        var element = driver.FindElement(GetBy(arguments));
        return ToolExecutionResult.Successful("Element found.", DescribeElement(element));
    }

    private ToolExecutionResult InspectPage(Guid testRunId, IWebDriver driver, JsonObject arguments)
    {
        var maxElements = Math.Clamp(arguments["max_elements"]?.GetValue<int>() ?? 40, 1, 100);
        var includeHidden = arguments["include_hidden"]?.GetValue<bool>() ?? false;
        var candidates = driver
            .FindElements(By.CssSelector("a,button,input,textarea,select,[role='button'],[role='link'],[onclick]"))
            .Where(element => includeHidden || SafeGetBoolean(() => element.Displayed))
            .Take(maxElements + 1)
            .ToArray();
        var returnedCandidates = candidates.Take(maxElements).ToArray();

        var refs = new Dictionary<string, BrowserElementReference>(StringComparer.Ordinal);
        var elements = new JsonArray();
        for (var index = 0; index < returnedCandidates.Length; index++)
        {
            var element = returnedCandidates[index];
            var reference = $"e{index + 1}";
            try
            {
                ((IJavaScriptExecutor)driver).ExecuteScript(
                    "arguments[0].setAttribute('data-browser-testing-ref', arguments[1]);",
                    element,
                    reference);
            }
            catch
            {
                continue;
            }

            refs[reference] = new BrowserElementReference(reference, $"[data-browser-testing-ref='{reference}']", driver.Url);
            var description = DescribeElement(element);
            description["ref"] = reference;
            description["type"] = Truncate(SafeGet(() => element.GetAttribute("type")), 80);
            description["aria_label"] = Truncate(SafeGet(() => element.GetAttribute("aria-label")), 160);
            description["href"] = Truncate(SafeGet(() => element.GetAttribute("href")), 300);
            description["value"] = Truncate(SafeGet(() => element.GetAttribute("value")), 300);
            elements.Add(description);
        }

        elementReferences[testRunId] = refs;
        return ToolExecutionResult.Successful(
            "Page inspected.",
            new JsonObject
            {
                ["url"] = Truncate(driver.Url, 500),
                ["title"] = Truncate(driver.Title, 300),
                ["visible_text"] = Truncate(SafeGet(() => driver.FindElement(By.TagName("body")).Text), 4000),
                ["count"] = elements.Count,
                ["truncated"] = candidates.Length > maxElements,
                ["elements"] = elements,
            },
            "Use click_ref or type_ref with one of the returned refs.");
    }

    private ToolExecutionResult ClickRef(Guid testRunId, IWebDriver driver, JsonObject arguments)
    {
        var reference = GetString(arguments, "ref");
        if (string.IsNullOrWhiteSpace(reference))
        {
            return ToolExecutionResult.Failed("A ref is required.", hint: "Call inspect_page first, then pass a returned ref.");
        }

        if (!TryGetElementReference(testRunId, reference, out var elementReference))
        {
            return ToolExecutionResult.Failed("Element ref was not found.", hint: "Call inspect_page again and use a returned ref from the current page.");
        }

        if (!IsReferenceForCurrentPage(driver, elementReference))
        {
            return CreatePreviousPageRefResult(driver, elementReference);
        }

        return InteractByReference(driver, elementReference, element => element.Click(), "Element ref clicked.");
    }

    private ToolExecutionResult TypeRef(Guid testRunId, IWebDriver driver, JsonObject arguments)
    {
        var reference = GetString(arguments, "ref");
        var text = GetString(arguments, "text") ?? string.Empty;
        var clearFirst = arguments["clear_first"]?.GetValue<bool>() ?? true;
        if (string.IsNullOrWhiteSpace(reference))
        {
            return ToolExecutionResult.Failed("A ref is required.", hint: "Call inspect_page first, then pass a returned ref.");
        }

        if (!TryGetElementReference(testRunId, reference, out var elementReference))
        {
            return ToolExecutionResult.Failed("Element ref was not found.", hint: "Call inspect_page again and use a returned ref from the current page.");
        }

        if (!IsReferenceForCurrentPage(driver, elementReference))
        {
            return CreatePreviousPageRefResult(driver, elementReference);
        }

        return InteractByReference(
            driver,
            elementReference,
            element =>
            {
                if (clearFirst)
                {
                    element.Clear();
                }

                element.SendKeys(text);
            },
            "Text entered into element ref.",
            new JsonObject { ["text"] = Truncate(text, 500), ["ref"] = reference });
    }

    private bool TryGetElementReference(Guid testRunId, string reference, out BrowserElementReference elementReference)
    {
        if (elementReferences.TryGetValue(testRunId, out var refs) &&
            refs.TryGetValue(reference, out elementReference!))
        {
            return true;
        }

        elementReference = default!;
        return false;
    }

    private ToolExecutionResult FinalizeBrowserToolResult(
        Guid testRunId,
        string toolName,
        string? beforeUrl,
        IWebDriver driver,
        ToolExecutionResult result)
    {
        if (!result.Success)
        {
            return result;
        }

        var afterUrl = SafeGet(() => driver.Url);
        if (!ShouldInvalidateRefsAfterTool(toolName, beforeUrl, afterUrl))
        {
            return result;
        }

        elementReferences.Remove(testRunId);
        var data = EnsureObjectData(result);
        data["before_url"] = Truncate(beforeUrl, 500);
        data["current_url"] = Truncate(afterUrl, 500);
        data["refs_invalidated"] = true;

        AppendHint(result, "Page context changed; refs from earlier inspect_page results are now invalid. Call inspect_page before using click_ref or type_ref again.");
        return result;
    }

    private static bool ShouldInvalidateRefsAfterTool(string toolName, string? beforeUrl, string? afterUrl) =>
        toolName is "goto_url" or "back" or "forward" or "refresh" or "switch_tab"
        || !IsSamePageUrl(beforeUrl, afterUrl);

    private static JsonObject EnsureObjectData(ToolExecutionResult result)
    {
        if (result.Data is JsonObject data)
        {
            return data;
        }

        var replacement = new JsonObject();
        if (result.Data is not null)
        {
            replacement["result"] = result.Data.DeepClone();
        }

        result.Data = replacement;
        return replacement;
    }

    private static void AppendHint(ToolExecutionResult result, string hint)
    {
        result.Hint = string.IsNullOrWhiteSpace(result.Hint)
            ? hint
            : $"{result.Hint} {hint}";
    }

    private static bool IsReferenceForCurrentPage(IWebDriver driver, BrowserElementReference elementReference) =>
        IsSamePageUrl(elementReference.PageUrl, SafeGet(() => driver.Url));

    private static ToolExecutionResult CreatePreviousPageRefResult(IWebDriver driver, BrowserElementReference elementReference) =>
        ToolExecutionResult.Failed(
            "Element ref belongs to a previous page.",
            data: new JsonObject
            {
                ["ref"] = elementReference.Ref,
                ["inspected_url"] = Truncate(elementReference.PageUrl, 500),
                ["current_url"] = Truncate(SafeGet(() => driver.Url), 500),
            },
            hint: "Use refs only from the latest inspect_page result for the current URL. Call inspect_page on the current page before click_ref or type_ref.");

    private static ToolExecutionResult InteractByReference(
        IWebDriver driver,
        BrowserElementReference elementReference,
        Action<IWebElement> action,
        string summary,
        JsonObject? dataOverride = null)
    {
        try
        {
            var element = driver.FindElement(By.CssSelector(elementReference.CssSelector));
            var beforeAction = DescribeElement(element);
            beforeAction["ref"] = elementReference.Ref;
            ((IJavaScriptExecutor)driver).ExecuteScript("arguments[0].scrollIntoView({behavior:'instant', block:'center'});", element);
            action(element);
            return ToolExecutionResult.Successful(summary, dataOverride ?? beforeAction);
        }
        catch (StaleElementReferenceException)
        {
            try
            {
                var element = driver.FindElement(By.CssSelector(elementReference.CssSelector));
                var beforeAction = DescribeElement(element);
                beforeAction["ref"] = elementReference.Ref;
                ((IJavaScriptExecutor)driver).ExecuteScript("arguments[0].scrollIntoView({behavior:'instant', block:'center'});", element);
                action(element);
                return ToolExecutionResult.Successful(summary, dataOverride ?? beforeAction, "Retried after a stale element reference.");
            }
            catch (WebDriverException ex)
            {
                return ToolExecutionResult.Failed("Element ref could not be used after retry.", Truncate(ex.Message, 1000), hint: "Call inspect_page again and use a current ref.");
            }
        }
        catch (NoSuchElementException)
        {
            return ToolExecutionResult.Failed("Element ref is no longer present.", hint: "Call inspect_page again and use a current ref.");
        }
    }

    private static ToolExecutionResult Interact(IWebDriver driver, JsonObject arguments, Action<IWebElement> action, string summary)
    {
        var by = GetBy(arguments);
        try
        {
            var element = driver.FindElement(by);
            var beforeAction = DescribeElement(element);
            action(element);
            return ToolExecutionResult.Successful(summary, beforeAction);
        }
        catch (StaleElementReferenceException)
        {
            try
            {
                var element = driver.FindElement(by);
                var beforeAction = DescribeElement(element);
                action(element);
                return ToolExecutionResult.Successful(summary, beforeAction, "Retried after a stale element reference.");
            }
            catch (WebDriverException ex)
            {
                return ToolExecutionResult.Failed("Element could not be used after stale retry.", Truncate(ex.Message, 1000), hint: "Inspect the current page and use a current ref or selector.");
            }
        }
        catch (NoSuchElementException)
        {
            try
            {
                var element = driver.FindElement(by);
                var beforeAction = DescribeElement(element);
                action(element);
                return ToolExecutionResult.Successful(summary, beforeAction, "Retried after the first lookup missed.");
            }
            catch (WebDriverException ex)
            {
                return ToolExecutionResult.Failed("Element was not found after retry.", Truncate(ex.Message, 1000), hint: "Call inspect_page and use click_ref/type_ref, or try a different selector.");
            }
        }
    }

    private static ToolExecutionResult DoubleClick(IWebDriver driver, JsonObject arguments)
    {
        var element = driver.FindElement(GetBy(arguments));
        new Actions(driver).DoubleClick(element).Perform();
        return ToolExecutionResult.Successful("Element double clicked.", DescribeElement(element));
    }

    private static ToolExecutionResult TypeText(IWebDriver driver, JsonObject arguments)
    {
        var text = GetString(arguments, "text") ?? string.Empty;
        var clearFirst = arguments["clear_first"]?.GetValue<bool>() ?? true;
        var element = driver.FindElement(GetBy(arguments));
        if (clearFirst)
        {
            element.Clear();
        }

        element.SendKeys(text);
        return ToolExecutionResult.Successful("Text entered.", new JsonObject
        {
            ["text"] = Truncate(text, 500),
            ["truncated"] = text.Length > 500,
        });
    }

    private static ToolExecutionResult SendKeys(IWebDriver driver, JsonObject arguments)
    {
        var keys = GetString(arguments, "keys") ?? string.Empty;
        var element = driver.FindElement(GetBy(arguments));
        element.SendKeys(keys);
        return ToolExecutionResult.Successful("Keys sent.", new JsonObject
        {
            ["keys"] = Truncate(keys, 500),
            ["truncated"] = keys.Length > 500,
        });
    }

    private static ToolExecutionResult SelectOption(IWebDriver driver, JsonObject arguments)
    {
        var element = driver.FindElement(GetBy(arguments));
        var options = element.FindElements(By.TagName("option"));
        var match = options.FirstOrDefault(candidate =>
            string.Equals(candidate.Text.Trim(), GetString(arguments, "text"), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(candidate.GetAttribute("value"), GetString(arguments, "value"), StringComparison.OrdinalIgnoreCase));

        if (match is null)
        {
            return ToolExecutionResult.Failed("No matching option was found.");
        }

        match.Click();
        return ToolExecutionResult.Successful("Option selected.", new JsonObject
        {
            ["text"] = match.Text,
            ["value"] = match.GetAttribute("value"),
        });
    }

    private static ToolExecutionResult Hover(IWebDriver driver, JsonObject arguments)
    {
        var element = driver.FindElement(GetBy(arguments));
        new Actions(driver).MoveToElement(element).Perform();
        return ToolExecutionResult.Successful("Hovered element.", DescribeElement(element));
    }

    private static ToolExecutionResult ScrollIntoView(IWebDriver driver, JsonObject arguments)
    {
        var element = driver.FindElement(GetBy(arguments));
        ((IJavaScriptExecutor)driver).ExecuteScript("arguments[0].scrollIntoView({behavior:'instant', block:'center'});", element);
        return ToolExecutionResult.Successful("Scrolled element into view.", DescribeElement(element));
    }

    private static ToolExecutionResult ReadText(IWebDriver driver, JsonObject arguments)
    {
        var element = driver.FindElement(GetBy(arguments));
        var text = element.Text;
        const int maxLength = 4000;
        return ToolExecutionResult.Successful("Element text captured.", new JsonObject
        {
            ["text"] = Truncate(text, maxLength),
            ["truncated"] = text.Length > maxLength,
            ["total_length"] = text.Length,
        });
    }

    private static ToolExecutionResult ReadAttribute(IWebDriver driver, JsonObject arguments)
    {
        var attribute = GetString(arguments, "attribute") ?? "value";
        var element = driver.FindElement(GetBy(arguments));
        var value = element.GetAttribute(attribute);
        const int maxLength = 2000;
        return ToolExecutionResult.Successful("Attribute read.", new JsonObject
        {
            ["attribute"] = attribute,
            ["value"] = Truncate(value, maxLength),
            ["truncated"] = (value?.Length ?? 0) > maxLength,
            ["total_length"] = value?.Length ?? 0,
        });
    }

    private static ToolExecutionResult GetHtml(IWebDriver driver, JsonObject arguments)
    {
        IWebElement? element = null;
        try
        {
            element = driver.FindElement(GetBy(arguments));
        }
        catch
        {
        }

        var html = element?.GetAttribute("outerHTML") ?? driver.PageSource;
        const int maxLength = 12000;
        return ToolExecutionResult.Successful(
            "HTML captured.",
            new JsonObject
            {
                ["html"] = Truncate(html, maxLength),
                ["truncated"] = html.Length > maxLength,
                ["total_length"] = html.Length,
            },
            html.Length > maxLength ? "HTML output was capped. Prefer inspect_page for compact actionable refs." : null);
    }

    private ToolExecutionResult TakeScreenshot(IWebDriver driver, JsonObject arguments)
    {
        var name = GetString(arguments, "name");
        var safeName = string.IsNullOrWhiteSpace(name) ? $"shot-{DateTime.UtcNow:yyyyMMdd-HHmmss}" : name;
        var outputPath = Path.Combine(settings.ScreenshotDirectory, $"{safeName}.png");
        var screenshot = ((ITakesScreenshot)driver).GetScreenshot();
        screenshot.SaveAsFile(outputPath);
        return ToolExecutionResult.Successful("Screenshot saved.", new JsonObject { ["path"] = outputPath });
    }

    private static ToolExecutionResult ExecuteJavaScript(IWebDriver driver, JsonObject arguments)
    {
        var script = GetString(arguments, "script");
        if (string.IsNullOrWhiteSpace(script))
        {
            return ToolExecutionResult.Failed("A script is required.");
        }

        var js = (IJavaScriptExecutor)driver;
        var args = arguments["arguments"] is JsonArray array
            ? array.Select(node => node switch
            {
                JsonValue value when value.TryGetValue<string>(out var text) => (object?)text,
                JsonValue value when value.TryGetValue<bool>(out var flag) => flag,
                JsonValue value when value.TryGetValue<int>(out var integer) => integer,
                JsonValue value when value.TryGetValue<double>(out var number) => number,
                _ => node?.ToJsonString(),
            }).ToArray()
            : [];

        var result = js.ExecuteScript(script, args);
        var resultText = result?.ToString();
        const int maxLength = 4000;
        return ToolExecutionResult.Successful("JavaScript executed.", new JsonObject
        {
            ["result"] = Truncate(resultText, maxLength),
            ["truncated"] = (resultText?.Length ?? 0) > maxLength,
            ["total_length"] = resultText?.Length ?? 0,
        });
    }

    private static async Task<ToolExecutionResult> WaitForElement(IWebDriver driver, JsonObject arguments, CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromMilliseconds(arguments["timeout_ms"]?.GetValue<int>() ?? 10000);
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var element = driver.FindElement(GetBy(arguments));
                return ToolExecutionResult.Successful("Element became available.", DescribeElement(element));
            }
            catch
            {
                await Task.Delay(200, cancellationToken);
            }
        }

        return ToolExecutionResult.Failed("Timed out waiting for element.");
    }

    private static async Task<ToolExecutionResult> WaitForText(IWebDriver driver, JsonObject arguments, CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromMilliseconds(arguments["timeout_ms"]?.GetValue<int>() ?? 10000);
        var text = GetString(arguments, "text") ?? string.Empty;
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (driver.PageSource.Contains(text, StringComparison.OrdinalIgnoreCase))
            {
                return ToolExecutionResult.Successful("Text was found on the page.", new JsonObject
                {
                    ["text"] = Truncate(text, 500),
                    ["truncated"] = text.Length > 500,
                });
            }

            await Task.Delay(200, cancellationToken);
        }

        return ToolExecutionResult.Failed("Timed out waiting for text.");
    }

    private static async Task<ToolExecutionResult> WaitForNavigation(IWebDriver driver, JsonObject arguments, CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromMilliseconds(arguments["timeout_ms"]?.GetValue<int>() ?? 10000);
        var expected = GetString(arguments, "url_contains") ?? string.Empty;
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (driver.Url.Contains(expected, StringComparison.OrdinalIgnoreCase))
            {
                return ToolExecutionResult.Successful("Navigation condition satisfied.", new JsonObject
                {
                    ["url"] = Truncate(driver.Url, 500),
                    ["truncated"] = driver.Url.Length > 500,
                });
            }

            await Task.Delay(200, cancellationToken);
        }

        return ToolExecutionResult.Failed("Timed out waiting for navigation.");
    }

    private static async Task<ToolExecutionResult> Sleep(JsonObject arguments, CancellationToken cancellationToken)
    {
        var milliseconds = arguments["milliseconds"]?.GetValue<int>() ?? 500;
        await Task.Delay(milliseconds, cancellationToken);
        return ToolExecutionResult.Successful($"Slept for {milliseconds} ms.");
    }

    private static ToolExecutionResult GetCookies(IWebDriver driver)
    {
        var cookies = new JsonArray();
        foreach (var cookie in driver.Manage().Cookies.AllCookies)
        {
            cookies.Add(new JsonObject
            {
                ["name"] = cookie.Name,
                ["value"] = cookie.Value,
                ["domain"] = cookie.Domain,
                ["path"] = cookie.Path,
                ["expiry"] = cookie.Expiry?.ToString("O"),
            });
        }

        return ToolExecutionResult.Successful("Cookies retrieved.", cookies);
    }

    private static ToolExecutionResult SetCookie(IWebDriver driver, JsonObject arguments)
    {
        var name = GetString(arguments, "name");
        var value = GetString(arguments, "value");
        if (string.IsNullOrWhiteSpace(name))
        {
            return ToolExecutionResult.Failed("Cookie name is required.");
        }

        driver.Manage().Cookies.AddCookie(new Cookie(
            name,
            value ?? string.Empty,
            GetString(arguments, "domain"),
            GetString(arguments, "path") ?? "/",
            null));

        return ToolExecutionResult.Successful("Cookie set.", new JsonObject { ["name"] = name });
    }

    private static Task<ToolExecutionResult> ExecuteAsyncTool(IWebDriver driver, string toolName, JsonObject arguments, CancellationToken cancellationToken) =>
        toolName switch
        {
            "wait_for_element" => WaitForElement(driver, arguments, cancellationToken),
            "wait_for_text" => WaitForText(driver, arguments, cancellationToken),
            "wait_for_navigation" => WaitForNavigation(driver, arguments, cancellationToken),
            "sleep" => Sleep(arguments, cancellationToken),
            _ => Task.FromResult(ToolExecutionResult.Failed($"Unknown browser tool `{toolName}`.")),
        };

    private static By GetBy(JsonObject arguments)
    {
        var locator = arguments["locator"]?.AsObject() ?? throw new InvalidOperationException("A locator is required.");
        var strategy = locator["strategy"]?.GetValue<string>() ?? "css";
        var value = locator["value"]?.GetValue<string>() ?? throw new InvalidOperationException("Locator value is required.");
        return strategy switch
        {
            "css" => By.CssSelector(value),
            "xpath" => By.XPath(value),
            "id" => By.Id(value),
            "name" => By.Name(value),
            "class" => By.ClassName(value),
            "tag" => By.TagName(value),
            "link_text" => By.LinkText(value),
            "partial_link_text" => By.PartialLinkText(value),
            _ => throw new InvalidOperationException($"Unknown locator strategy `{strategy}`."),
        };
    }

    private static JsonObject DescribeElement(IWebElement element) =>
        new()
        {
            ["tag"] = Truncate(SafeGet(() => element.TagName), 80),
            ["text"] = Truncate(SafeGet(() => element.Text), 800),
            ["displayed"] = SafeGetBoolean(() => element.Displayed),
            ["enabled"] = SafeGetBoolean(() => element.Enabled),
            ["id"] = Truncate(SafeGet(() => element.GetAttribute("id")), 120),
            ["class"] = Truncate(SafeGet(() => element.GetAttribute("class")), 240),
            ["name"] = Truncate(SafeGet(() => element.GetAttribute("name")), 120),
        };

    private static JsonObject SnapshotNode(BrowserSessionSnapshot snapshot)
    {
        var tabs = new JsonArray();
        foreach (var tab in snapshot.Tabs)
        {
            tabs.Add(new JsonObject
            {
                ["handle"] = tab.Handle,
                ["title"] = Truncate(tab.Title, 300),
                ["url"] = Truncate(tab.Url, 500),
                ["is_selected"] = tab.IsSelected,
            });
        }

        return new JsonObject
        {
            ["current_url"] = Truncate(snapshot.CurrentUrl, 500),
            ["page_title"] = Truncate(snapshot.PageTitle, 300),
            ["profile_path"] = snapshot.ProfilePath,
            ["restore_status"] = snapshot.RestoreStatus.ToString(),
            ["tabs"] = tabs,
        };
    }

    private static string? GetString(JsonObject arguments, string name) =>
        arguments[name] is JsonValue value && value.TryGetValue<string>(out var result)
            ? result
            : null;

    private BrowserSessionSnapshot CaptureAndCacheSnapshotLocked(BrowserSession session, RestoreStatus restoreStatus)
    {
        var snapshot = CaptureSnapshot(session, restoreStatus);
        snapshots[session.TestRunId] = snapshot;
        return CloneSnapshot(snapshot);
    }

    private BrowserSessionSnapshot MarkActiveSessionClosedLocked()
    {
        if (activeSession is null)
        {
            return new BrowserSessionSnapshot { RestoreStatus = RestoreStatus.Closed, LastCapturedAtUtc = DateTime.UtcNow };
        }

        var session = activeSession;
        activeSession = null;

        var snapshot = BuildClosedSnapshot(session.TestRunId, session.ProfilePath);
        snapshots[session.TestRunId] = snapshot;
        DisposeSession(session);
        return CloneSnapshot(snapshot);
    }

    private void CloseActiveSessionLocked()
    {
        if (activeSession is null)
        {
            return;
        }

        var session = activeSession;
        activeSession = null;
        snapshots[session.TestRunId] = BuildClosedSnapshot(session.TestRunId, session.ProfilePath);
        DisposeSession(session);
    }

    private static void DisposeSession(BrowserSession session)
    {
        try
        {
            session.Driver.Quit();
        }
        catch
        {
        }

        try
        {
            session.Driver.Dispose();
        }
        catch
        {
        }

        try
        {
            session.Service.Dispose();
        }
        catch
        {
        }
    }

    private static bool IsSessionAlive(BrowserSession session)
    {
        try
        {
            return session.Driver.WindowHandles.Count > 0;
        }
        catch (Exception ex) when (IsClosedBrowserException(ex))
        {
            return false;
        }
    }

    private static bool IsClosedBrowserException(Exception ex) =>
        ex is NoSuchWindowException
        || ex is ObjectDisposedException
        || ex is InvalidOperationException invalidOperationException && ContainsClosedBrowserText(invalidOperationException.Message)
        || ex is WebDriverException webDriverException && ContainsClosedBrowserText(webDriverException.Message);

    private static bool ContainsClosedBrowserText(string? message) =>
        !string.IsNullOrWhiteSpace(message) && (
            message.Contains("no such window", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("target window already closed", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("web view not found", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("invalid session id", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("chrome not reachable", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("disconnected", StringComparison.OrdinalIgnoreCase));

    private static bool IsSamePageUrl(string? left, string? right)
    {
        var normalizedLeft = NormalizePageUrl(left);
        var normalizedRight = NormalizePageUrl(right);
        return !string.IsNullOrWhiteSpace(normalizedLeft) &&
               !string.IsNullOrWhiteSpace(normalizedRight) &&
               string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizePageUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var hashIndex = url.IndexOf('#', StringComparison.Ordinal);
            return (hashIndex >= 0 ? url[..hashIndex] : url).TrimEnd('/');
        }

        var builder = new UriBuilder(uri)
        {
            Fragment = string.Empty,
        };
        return builder.Uri.AbsoluteUri.TrimEnd('/');
    }

    private static BrowserSessionSnapshot CaptureSnapshot(BrowserSession session, RestoreStatus restoreStatus)
    {
        var driver = session.Driver;
        var tabs = new List<BrowserTabInfo>();
        var originalHandle = SafeGet(() => driver.CurrentWindowHandle);

        foreach (var handle in driver.WindowHandles)
        {
            driver.SwitchTo().Window(handle);
            tabs.Add(new BrowserTabInfo
            {
                Handle = handle,
                Title = driver.Title,
                Url = driver.Url,
                IsSelected = handle == originalHandle,
            });
        }

        if (!string.IsNullOrWhiteSpace(originalHandle) && driver.WindowHandles.Contains(originalHandle))
        {
            driver.SwitchTo().Window(originalHandle);
        }

        return new BrowserSessionSnapshot
        {
            TestRunId = session.TestRunId,
            ProfilePath = session.ProfilePath,
            CurrentUrl = SafeGet(() => driver.Url),
            PageTitle = SafeGet(() => driver.Title),
            DriverSessionId = session.Driver.SessionId?.ToString(),
            DriverServiceUrl = session.Service.ServiceUrl?.ToString(),
            BrowserProcessId = session.Service.ProcessId,
            RestoreStatus = restoreStatus,
            LastCapturedAtUtc = DateTime.UtcNow,
            Tabs = tabs,
        };
    }

    private static BrowserSessionSnapshot BuildClosedSnapshot(Guid testRunId, string? profilePath) =>
        new()
        {
            TestRunId = testRunId,
            ProfilePath = profilePath,
            CurrentUrl = null,
            PageTitle = null,
            DriverSessionId = null,
            DriverServiceUrl = null,
            BrowserProcessId = null,
            RestoreStatus = RestoreStatus.Closed,
            LastCapturedAtUtc = DateTime.UtcNow,
            Tabs = [],
        };

    private static BrowserSessionSnapshot CloseSnapshot(BrowserSessionSnapshot snapshot)
    {
        var copy = CloneSnapshot(snapshot);
        copy.CurrentUrl = null;
        copy.PageTitle = null;
        copy.DriverSessionId = null;
        copy.DriverServiceUrl = null;
        copy.BrowserProcessId = null;
        copy.RestoreStatus = RestoreStatus.Closed;
        copy.LastCapturedAtUtc = DateTime.UtcNow;
        copy.Tabs.Clear();
        return copy;
    }

    private static BrowserSessionSnapshot CloneSnapshot(BrowserSessionSnapshot snapshot) =>
        new()
        {
            TestRunId = snapshot.TestRunId,
            ProfilePath = snapshot.ProfilePath,
            CurrentUrl = snapshot.CurrentUrl,
            PageTitle = snapshot.PageTitle,
            DriverSessionId = snapshot.DriverSessionId,
            DriverServiceUrl = snapshot.DriverServiceUrl,
            BrowserProcessId = snapshot.BrowserProcessId,
            RestoreStatus = snapshot.RestoreStatus,
            LastCapturedAtUtc = snapshot.LastCapturedAtUtc,
            Tabs = snapshot.Tabs.Select(tab => new BrowserTabInfo
            {
                Handle = tab.Handle,
                Title = tab.Title,
                Url = tab.Url,
                IsSelected = tab.IsSelected,
            }).ToList(),
        };

    private static ToolExecutionResult CreateNoActiveBrowserResult(BrowserSessionSnapshot? persistedSnapshot)
    {
        if (persistedSnapshot?.RestoreStatus == RestoreStatus.Closed)
        {
            return ToolExecutionResult.Failed("Browser window is closed.", "Use `open_browser` to launch a new session.");
        }

        return ToolExecutionResult.Failed("No active browser session for this run.", "Use `open_browser` to launch Chrome before browser commands.");
    }

    private static string? SafeGet(Func<string?> getter)
    {
        try
        {
            return getter();
        }
        catch
        {
            return null;
        }
    }

    private static bool SafeGetBoolean(Func<bool> getter, bool fallback = false)
    {
        try
        {
            return getter();
        }
        catch
        {
            return fallback;
        }
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (value is null || value.Length <= maxLength)
        {
            return value;
        }

        return value[..maxLength];
    }

    private async Task<T> RunLockedAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        await sessionGate.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(action, cancellationToken);
        }
        finally
        {
            sessionGate.Release();
        }
    }

    private async Task<T> RunLockedAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        await sessionGate.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(action, cancellationToken);
        }
        finally
        {
            sessionGate.Release();
        }
    }

    private async Task RunLockedAsync(Action action, CancellationToken cancellationToken)
    {
        await sessionGate.WaitAsync(cancellationToken);
        try
        {
            await Task.Run(action, cancellationToken);
        }
        finally
        {
            sessionGate.Release();
        }
    }

    private sealed record BrowserSession(Guid TestRunId, string ProfilePath, ChromeDriverService Service, ChromeDriver Driver);

    private sealed record BrowserElementReference(string Ref, string CssSelector, string PageUrl);
}
