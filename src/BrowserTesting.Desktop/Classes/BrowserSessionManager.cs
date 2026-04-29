using System.Diagnostics;
using System.Text.Json.Nodes;
using BrowserTesting.Desktop.Models;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Interactions;

namespace BrowserTesting.Desktop.Classes;

public sealed class BrowserSessionManager(AppSettings settings)
{
    private readonly SemaphoreSlim sessionGate = new(1, 1);
    private readonly Dictionary<Guid, BrowserSessionSnapshot> snapshots = [];
    private readonly Dictionary<Guid, Dictionary<string, BrowserElementReference>> elementReferences = [];
    private BrowserSession? activeSession;

    public Task<BrowserSessionSnapshot?> GetSnapshotAsync(Guid testRunId, CancellationToken cancellationToken) =>
        Locked(() =>
        {
            if (activeSession?.TestRunId == testRunId)
            {
                return !IsSessionAlive(activeSession)
                    ? MarkActiveSessionClosedLocked()
                    : CaptureAndCacheSnapshotLocked(activeSession, BrowserState.Active);
            }

            return snapshots.TryGetValue(testRunId, out var snapshot) ? CloneSnapshot(snapshot) : null;
        }, cancellationToken);

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
            return await OpenBrowserAsync(testRunId, arguments, headless, cancellationToken);
        }

        if (toolName == "close_browser")
        {
            await CloseBrowserAsync(testRunId, cancellationToken);
            return ToolExecutionResult.Successful("Browser closed.");
        }

        return await Locked(async () =>
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
                    "goto_url" => NavigateTo(driver, arguments),
                    "back" => Navigate(driver, d => d.Navigate().Back(), "Navigated back."),
                    "forward" => Navigate(driver, d => d.Navigate().Forward(), "Navigated forward."),
                    "refresh" => Navigate(driver, d => d.Navigate().Refresh(), "Page refreshed."),
                    "get_page_state" => ToolExecutionResult.Successful("Page state captured.", SnapshotNode(CaptureAndCacheSnapshotLocked(session, BrowserState.Active))),
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
                    "execute_javascript" => ExecuteJavaScript(driver, arguments),
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

    public Task CloseBrowserAsync(Guid testRunId, CancellationToken cancellationToken) =>
        Locked(() =>
        {
            if (activeSession?.TestRunId == testRunId)
            {
                MarkActiveSessionClosedLocked();
            }
            else if (snapshots.TryGetValue(testRunId, out var snapshot))
            {
                snapshots[testRunId] = CloseSnapshot(snapshot);
            }
        }, cancellationToken);

    private Task<ToolExecutionResult> OpenBrowserAsync(Guid testRunId, JsonObject arguments, bool headless, CancellationToken cancellationToken) =>
        Locked(() =>
        {
            var startUrl = GetString(arguments, "url");
            var profilePath = Path.Combine(settings.ChromeProfileRoot, GetString(arguments, "profile_name") ?? testRunId.ToString("N"));
            if (activeSession?.TestRunId == testRunId && IsSessionAlive(activeSession))
            {
                if (!string.IsNullOrWhiteSpace(startUrl) && !string.Equals(SafeGet(() => activeSession.Driver.Url), startUrl, StringComparison.OrdinalIgnoreCase))
                {
                    activeSession.Driver.Navigate().GoToUrl(startUrl);
                    elementReferences.Remove(testRunId);
                }

                var current = CaptureAndCacheSnapshotLocked(activeSession, BrowserState.Active);
                return ToolExecutionResult.Successful("Chrome already open.", SnapshotNode(current), "The active browser session was reused. Continue with page tools; do not call open_browser again unless the browser closes.");
            }

            if (activeSession?.TestRunId == testRunId)
            {
                MarkActiveSessionClosedLocked();
            }

            CloseActiveSessionLocked();
            activeSession = CreateSession(testRunId, profilePath, headless);
            elementReferences.Remove(testRunId);
            if (!string.IsNullOrWhiteSpace(startUrl))
            {
                activeSession.Driver.Navigate().GoToUrl(startUrl);
            }

            return ToolExecutionResult.Successful("Chrome opened.", SnapshotNode(CaptureAndCacheSnapshotLocked(activeSession, BrowserState.Active)));
        }, cancellationToken);

    private BrowserSession CreateSession(Guid testRunId, string profilePath, bool headless)
    {
        Directory.CreateDirectory(settings.ChromeProfileRoot);
        Directory.CreateDirectory(profilePath);
        var options = new ChromeOptions();
        foreach (var argument in new[] { "--disable-gpu", $"--user-data-dir={profilePath}", "--window-size=1920,1080" })
        {
            options.AddArgument(argument);
        }

        if (headless)
        {
            options.AddArgument("--headless=new");
        }
        else
        {
            options.AddArgument("--no-first-run");
            options.AddArgument("--no-default-browser-check");
        }

        var service = ChromeDriverService.CreateDefaultService();
        service.HideCommandPromptWindow = true;
        var driver = new ChromeDriver(service, options);
        CloseExtraWindows(driver);
        return new(testRunId, profilePath, service, driver);
    }

    private static void CloseExtraWindows(ChromeDriver driver)
    {
        var primary = driver.CurrentWindowHandle;
        foreach (var handle in driver.WindowHandles.Where(handle => handle != primary).ToArray())
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

        if (driver.WindowHandles.Contains(primary))
        {
            driver.SwitchTo().Window(primary);
        }
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
        if (!many)
        {
            return ToolExecutionResult.Successful("Element found.", DescribeElement(driver.FindElement(GetBy(arguments))));
        }

        var elements = driver.FindElements(GetBy(arguments));
        const int maxReturned = 40;
        return ToolExecutionResult.Successful($"Found {elements.Count} elements.", new JsonObject
        {
            ["count"] = elements.Count,
            ["truncated"] = elements.Count > maxReturned,
            ["elements"] = new JsonArray(elements.Take(maxReturned).Select(DescribeElement).ToArray()),
        }, elements.Count > maxReturned ? $"Result capped at {maxReturned} elements. Use inspect_page for compact page refs." : null);
    }

    private ToolExecutionResult InspectPage(Guid testRunId, IWebDriver driver, JsonObject arguments)
    {
        var maxElements = Math.Clamp(arguments["max_elements"]?.GetValue<int>() ?? 40, 1, 100);
        var includeHidden = arguments["include_hidden"]?.GetValue<bool>() ?? false;
        var candidates = driver.FindElements(By.CssSelector("a,button,input,textarea,select,[role='button'],[role='link'],[onclick]"))
            .Where(element => includeHidden || SafeGetBoolean(() => element.Displayed))
            .Take(maxElements + 1)
            .ToArray();
        var refs = new Dictionary<string, BrowserElementReference>(StringComparer.Ordinal);
        var elements = new JsonArray();
        for (var index = 0; index < Math.Min(candidates.Length, maxElements); index++)
        {
            var element = candidates[index];
            var reference = $"e{index + 1}";
            try
            {
                ((IJavaScriptExecutor)driver).ExecuteScript("arguments[0].setAttribute('data-browser-testing-ref', arguments[1]);", element, reference);
            }
            catch
            {
                continue;
            }

            refs[reference] = new(reference, $"[data-browser-testing-ref='{reference}']", driver.Url);
            var description = DescribeElement(element);
            foreach (var pair in new Dictionary<string, string?> { ["ref"] = reference, ["type"] = SafeGet(() => element.GetAttribute("type")), ["aria_label"] = SafeGet(() => element.GetAttribute("aria-label")), ["href"] = SafeGet(() => element.GetAttribute("href")), ["value"] = SafeGet(() => element.GetAttribute("value")) })
            {
                description[pair.Key] = Truncate(pair.Value, pair.Key == "href" || pair.Key == "value" ? 300 : 160);
            }

            elements.Add(description);
        }

        elementReferences[testRunId] = refs;
        return ToolExecutionResult.Successful("Page inspected.", new JsonObject
        {
            ["url"] = Truncate(driver.Url, 500),
            ["title"] = Truncate(driver.Title, 300),
            ["visible_text"] = Truncate(SafeGet(() => driver.FindElement(By.TagName("body")).Text), 4000),
            ["count"] = elements.Count,
            ["truncated"] = candidates.Length > maxElements,
            ["elements"] = elements,
        }, "Use click_ref or type_ref with one of the returned refs.");
    }

    private ToolExecutionResult ClickRef(Guid testRunId, IWebDriver driver, JsonObject arguments)
    {
        if (!TryGetCurrentReference(testRunId, driver, GetString(arguments, "ref"), out var reference, out var failure))
        {
            return failure!;
        }

        return InteractByReference(driver, reference, element => element.Click(), "Element ref clicked.");
    }

    private ToolExecutionResult TypeRef(Guid testRunId, IWebDriver driver, JsonObject arguments)
    {
        if (!TryGetCurrentReference(testRunId, driver, GetString(arguments, "ref"), out var reference, out var failure))
        {
            return failure!;
        }

        var text = GetString(arguments, "text") ?? string.Empty;
        return InteractByReference(driver, reference, element =>
        {
            if (arguments["clear_first"]?.GetValue<bool>() ?? true)
            {
                element.Clear();
            }

            element.SendKeys(text);
        }, "Text entered into element ref.", new JsonObject { ["text"] = Truncate(text, 500), ["ref"] = reference.Ref });
    }

    private bool TryGetCurrentReference(Guid testRunId, IWebDriver driver, string? refName, out BrowserElementReference reference, out ToolExecutionResult? failure)
    {
        reference = default!;
        failure = null;
        if (string.IsNullOrWhiteSpace(refName))
        {
            failure = ToolExecutionResult.Failed("A ref is required.", hint: "Call inspect_page first, then pass a returned ref.");
        }
        else if (!elementReferences.TryGetValue(testRunId, out var refs) || !refs.TryGetValue(refName, out reference!))
        {
            failure = ToolExecutionResult.Failed("Element ref was not found.", hint: "Call inspect_page again and use a returned ref from the current page.");
        }
        else if (!IsSamePageUrl(reference.PageUrl, SafeGet(() => driver.Url)))
        {
            failure = ToolExecutionResult.Failed("Element ref belongs to a previous page.", data: new JsonObject
            {
                ["ref"] = reference.Ref,
                ["inspected_url"] = Truncate(reference.PageUrl, 500),
                ["current_url"] = Truncate(SafeGet(() => driver.Url), 500),
            }, hint: "Use refs only from the latest inspect_page result for the current URL. Call inspect_page on the current page before click_ref or type_ref.");
        }

        return failure is null;
    }

    private ToolExecutionResult FinalizeBrowserToolResult(Guid testRunId, string toolName, string? beforeUrl, IWebDriver driver, ToolExecutionResult result)
    {
        if (!result.Success || !ShouldInvalidateRefsAfterTool(toolName, beforeUrl, SafeGet(() => driver.Url)))
        {
            return result;
        }

        elementReferences.Remove(testRunId);
        var data = EnsureObjectData(result);
        data["before_url"] = Truncate(beforeUrl, 500);
        data["current_url"] = Truncate(SafeGet(() => driver.Url), 500);
        data["refs_invalidated"] = true;
        result.Hint = string.IsNullOrWhiteSpace(result.Hint)
            ? "Page context changed; refs from earlier inspect_page results are now invalid. Call inspect_page before using click_ref or type_ref again."
            : $"{result.Hint} Page context changed; refs from earlier inspect_page results are now invalid. Call inspect_page before using click_ref or type_ref again.";
        return result;
    }

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

    private static ToolExecutionResult InteractByReference(IWebDriver driver, BrowserElementReference reference, Action<IWebElement> action, string summary, JsonObject? dataOverride = null) =>
        UseElement(() => driver.FindElement(By.CssSelector(reference.CssSelector)), element =>
        {
            var data = DescribeElement(element);
            data["ref"] = reference.Ref;
            ((IJavaScriptExecutor)driver).ExecuteScript("arguments[0].scrollIntoView({behavior:'instant', block:'center'});", element);
            return data;
        }, action, summary, dataOverride, retryMissing: false, failureHint: "Call inspect_page again and use a current ref.");

    private static ToolExecutionResult Interact(IWebDriver driver, JsonObject arguments, Action<IWebElement> action, string summary) =>
        UseElement(() => driver.FindElement(GetBy(arguments)), DescribeElement, action, summary);

    private static ToolExecutionResult UseElement(Func<IWebElement> find, Func<IWebElement, JsonObject> describe, Action<IWebElement> action, string summary, JsonObject? dataOverride = null, bool retryMissing = true, string failureHint = "Call inspect_page and use click_ref/type_ref, or try a different selector.")
    {
        ToolExecutionResult Use(string? hint = null)
        {
            var element = find();
            var data = describe(element);
            action(element);
            return ToolExecutionResult.Successful(summary, dataOverride ?? data, hint);
        }

        try
        {
            return Use();
        }
        catch (StaleElementReferenceException)
        {
            try
            {
                return Use("Retried after a stale element reference.");
            }
            catch (WebDriverException ex)
            {
                return ToolExecutionResult.Failed("Element could not be used after retry.", Truncate(ex.Message, 1000), hint: failureHint);
            }
        }
        catch (NoSuchElementException) when (retryMissing)
        {
            try
            {
                return Use("Retried after the first lookup missed.");
            }
            catch (WebDriverException ex)
            {
                return ToolExecutionResult.Failed("Element was not found after retry.", Truncate(ex.Message, 1000), hint: failureHint);
            }
        }
        catch (NoSuchElementException)
        {
            return ToolExecutionResult.Failed("Element ref is no longer present.", hint: failureHint);
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
        var element = driver.FindElement(GetBy(arguments));
        if (arguments["clear_first"]?.GetValue<bool>() ?? true)
        {
            element.Clear();
        }

        element.SendKeys(text);
        return TextPayload("Text entered.", "text", text);
    }

    private static ToolExecutionResult SendKeys(IWebDriver driver, JsonObject arguments)
    {
        var keys = GetString(arguments, "keys") ?? string.Empty;
        driver.FindElement(GetBy(arguments)).SendKeys(keys);
        return TextPayload("Keys sent.", "keys", keys);
    }

    private static ToolExecutionResult SelectOption(IWebDriver driver, JsonObject arguments)
    {
        var options = driver.FindElement(GetBy(arguments)).FindElements(By.TagName("option"));
        var match = options.FirstOrDefault(candidate =>
            string.Equals(candidate.Text.Trim(), GetString(arguments, "text"), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(candidate.GetAttribute("value"), GetString(arguments, "value"), StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            return ToolExecutionResult.Failed("No matching option was found.");
        }

        match.Click();
        return ToolExecutionResult.Successful("Option selected.", new JsonObject { ["text"] = match.Text, ["value"] = match.GetAttribute("value") });
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

    private static ToolExecutionResult ReadText(IWebDriver driver, JsonObject arguments) =>
        TextPayload("Element text captured.", "text", driver.FindElement(GetBy(arguments)).Text, 4000);

    private static ToolExecutionResult ReadAttribute(IWebDriver driver, JsonObject arguments)
    {
        var attribute = GetString(arguments, "attribute") ?? "value";
        var value = driver.FindElement(GetBy(arguments)).GetAttribute(attribute);
        var data = TruncatedPayload("value", value, 2000);
        data["attribute"] = attribute;
        return ToolExecutionResult.Successful("Attribute read.", data);
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
        return ToolExecutionResult.Successful("HTML captured.", TruncatedPayload("html", html, 12000), html.Length > 12000 ? "HTML output was capped. Prefer inspect_page for compact actionable refs." : null);
    }

    private static ToolExecutionResult ExecuteJavaScript(IWebDriver driver, JsonObject arguments)
    {
        var script = GetString(arguments, "script");
        if (string.IsNullOrWhiteSpace(script))
        {
            return ToolExecutionResult.Failed("A script is required.");
        }

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
        return ToolExecutionResult.Successful("JavaScript executed.", TruncatedPayload("result", ((IJavaScriptExecutor)driver).ExecuteScript(script, args)?.ToString(), 4000));
    }

    private static async Task<ToolExecutionResult> ExecuteAsyncTool(IWebDriver driver, string toolName, JsonObject arguments, CancellationToken cancellationToken) =>
        toolName switch
        {
            "wait_for_element" => await WaitFor(driver, arguments, cancellationToken, "Timed out waiting for element.", () =>
            {
                var element = driver.FindElement(GetBy(arguments));
                return ToolExecutionResult.Successful("Element became available.", DescribeElement(element));
            }),
            "wait_for_text" => await WaitFor(driver, arguments, cancellationToken, "Timed out waiting for text.", () =>
                driver.PageSource.Contains(GetString(arguments, "text") ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                    ? TextPayload("Text was found on the page.", "text", GetString(arguments, "text") ?? string.Empty)
                    : null),
            "wait_for_navigation" => await WaitFor(driver, arguments, cancellationToken, "Timed out waiting for navigation.", () =>
                driver.Url.Contains(GetString(arguments, "url_contains") ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                    ? ToolExecutionResult.Successful("Navigation condition satisfied.", TruncatedPayload("url", driver.Url, 500))
                    : null),
            "sleep" => await Sleep(arguments, cancellationToken),
            _ => ToolExecutionResult.Failed($"Unknown browser tool `{toolName}`."),
        };

    private static async Task<ToolExecutionResult> WaitFor(IWebDriver driver, JsonObject arguments, CancellationToken cancellationToken, string timeoutSummary, Func<ToolExecutionResult?> condition)
    {
        var timeout = TimeSpan.FromMilliseconds(arguments["timeout_ms"]?.GetValue<int>() ?? 10000);
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (condition() is { } result)
                {
                    return result;
                }
            }
            catch
            {
            }

            await Task.Delay(200, cancellationToken);
        }

        return ToolExecutionResult.Failed(timeoutSummary);
    }

    private static async Task<ToolExecutionResult> Sleep(JsonObject arguments, CancellationToken cancellationToken)
    {
        var milliseconds = arguments["milliseconds"]?.GetValue<int>() ?? 500;
        await Task.Delay(milliseconds, cancellationToken);
        return ToolExecutionResult.Successful($"Slept for {milliseconds} ms.");
    }

    private static By GetBy(JsonObject arguments)
    {
        var locator = arguments["locator"]?.AsObject() ?? throw new InvalidOperationException("A locator is required.");
        var value = locator["value"]?.GetValue<string>() ?? throw new InvalidOperationException("Locator value is required.");
        return (locator["strategy"]?.GetValue<string>() ?? "css") switch
        {
            "css" => By.CssSelector(value),
            "xpath" => By.XPath(value),
            "id" => By.Id(value),
            "name" => By.Name(value),
            "class" => By.ClassName(value),
            "tag" => By.TagName(value),
            "link_text" => By.LinkText(value),
            "partial_link_text" => By.PartialLinkText(value),
            var strategy => throw new InvalidOperationException($"Unknown locator strategy `{strategy}`."),
        };
    }

    private static JsonObject DescribeElement(IWebElement element) => new()
    {
        ["tag"] = Truncate(SafeGet(() => element.TagName), 80),
        ["text"] = Truncate(SafeGet(() => element.Text), 800),
        ["displayed"] = SafeGetBoolean(() => element.Displayed),
        ["enabled"] = SafeGetBoolean(() => element.Enabled),
        ["id"] = Truncate(SafeGet(() => element.GetAttribute("id")), 120),
        ["class"] = Truncate(SafeGet(() => element.GetAttribute("class")), 240),
        ["name"] = Truncate(SafeGet(() => element.GetAttribute("name")), 120),
    };

    private static ToolExecutionResult TextPayload(string summary, string name, string? value, int maxLength = 500) =>
        ToolExecutionResult.Successful(summary, TruncatedPayload(name, value, maxLength));

    private static JsonObject TruncatedPayload(string name, string? value, int maxLength) => new()
    {
        [name] = Truncate(value, maxLength),
        ["truncated"] = (value?.Length ?? 0) > maxLength,
        ["total_length"] = value?.Length ?? 0,
    };

    private static JsonObject SnapshotNode(BrowserSessionSnapshot snapshot) => new()
    {
        ["current_url"] = Truncate(snapshot.CurrentUrl, 500),
        ["page_title"] = Truncate(snapshot.PageTitle, 300),
        ["state"] = snapshot.State.ToString(),
        ["tabs"] = new JsonArray(snapshot.Tabs.Select(tab => new JsonObject
        {
            ["handle"] = tab.Handle,
            ["title"] = Truncate(tab.Title, 300),
            ["url"] = Truncate(tab.Url, 500),
            ["is_selected"] = tab.IsSelected,
        }).ToArray()),
    };

    private BrowserSessionSnapshot CaptureAndCacheSnapshotLocked(BrowserSession session, BrowserState state)
    {
        var snapshot = CaptureSnapshot(session, state);
        snapshots[session.TestRunId] = snapshot;
        return CloneSnapshot(snapshot);
    }

    private BrowserSessionSnapshot MarkActiveSessionClosedLocked()
    {
        if (activeSession is null)
        {
            return new BrowserSessionSnapshot { State = BrowserState.Closed, LastCapturedAtUtc = DateTime.UtcNow };
        }

        var session = activeSession;
        activeSession = null;
        var snapshot = BuildClosedSnapshot(session.TestRunId);
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
        snapshots[session.TestRunId] = BuildClosedSnapshot(session.TestRunId);
        DisposeSession(session);
    }

    private static void DisposeSession(BrowserSession session)
    {
        foreach (var dispose in new Action[] { () => session.Driver.Quit(), () => session.Driver.Dispose(), () => session.Service.Dispose() })
        {
            try
            {
                dispose();
            }
            catch
            {
            }
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

    private static bool ShouldInvalidateRefsAfterTool(string toolName, string? beforeUrl, string? afterUrl) =>
        toolName is "goto_url" or "back" or "forward" or "refresh" || !IsSamePageUrl(beforeUrl, afterUrl);

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

        return new UriBuilder(uri) { Fragment = string.Empty }.Uri.AbsoluteUri.TrimEnd('/');
    }

    private static BrowserSessionSnapshot CaptureSnapshot(BrowserSession session, BrowserState state)
    {
        var driver = session.Driver;
        var tabs = new List<BrowserTabInfo>();
        var originalHandle = SafeGet(() => driver.CurrentWindowHandle);
        foreach (var handle in driver.WindowHandles)
        {
            driver.SwitchTo().Window(handle);
            tabs.Add(new BrowserTabInfo { Handle = handle, Title = driver.Title, Url = driver.Url, IsSelected = handle == originalHandle });
        }

        if (!string.IsNullOrWhiteSpace(originalHandle) && driver.WindowHandles.Contains(originalHandle))
        {
            driver.SwitchTo().Window(originalHandle);
        }

        return new BrowserSessionSnapshot { TestRunId = session.TestRunId, CurrentUrl = SafeGet(() => driver.Url), PageTitle = SafeGet(() => driver.Title), State = state, LastCapturedAtUtc = DateTime.UtcNow, Tabs = tabs };
    }

    private static BrowserSessionSnapshot BuildClosedSnapshot(Guid testRunId) => new() { TestRunId = testRunId, State = BrowserState.Closed, LastCapturedAtUtc = DateTime.UtcNow, Tabs = [] };

    private static BrowserSessionSnapshot CloseSnapshot(BrowserSessionSnapshot snapshot)
    {
        var copy = CloneSnapshot(snapshot);
        copy.CurrentUrl = null;
        copy.PageTitle = null;
        copy.State = BrowserState.Closed;
        copy.LastCapturedAtUtc = DateTime.UtcNow;
        copy.Tabs.Clear();
        return copy;
    }

    private static BrowserSessionSnapshot CloneSnapshot(BrowserSessionSnapshot snapshot) => new()
    {
        TestRunId = snapshot.TestRunId,
        CurrentUrl = snapshot.CurrentUrl,
        PageTitle = snapshot.PageTitle,
        State = snapshot.State,
        LastCapturedAtUtc = snapshot.LastCapturedAtUtc,
        Tabs = snapshot.Tabs.Select(tab => new BrowserTabInfo { Handle = tab.Handle, Title = tab.Title, Url = tab.Url, IsSelected = tab.IsSelected }).ToList(),
    };

    private static ToolExecutionResult CreateNoActiveBrowserResult(BrowserSessionSnapshot? persistedSnapshot) =>
        persistedSnapshot?.State == BrowserState.Closed
            ? ToolExecutionResult.Failed("Browser window is closed.", "Use `open_browser` to launch a new session.")
            : ToolExecutionResult.Failed("No active browser session for this run.", "Use `open_browser` to launch Chrome before browser commands.");

    private static string? GetString(JsonObject arguments, string name) =>
        arguments[name] is JsonValue value && value.TryGetValue<string>(out var result) ? result : null;

    private static string? SafeGet(Func<string?> getter)
    {
        try { return getter(); } catch { return null; }
    }

    private static bool SafeGetBoolean(Func<bool> getter, bool fallback = false)
    {
        try { return getter(); } catch { return fallback; }
    }

    private static string? Truncate(string? value, int maxLength) =>
        value is null || value.Length <= maxLength ? value : value[..maxLength];

    private async Task<T> Locked<T>(Func<T> action, CancellationToken cancellationToken)
    {
        await sessionGate.WaitAsync(cancellationToken);
        try { return await Task.Run(action, cancellationToken); }
        finally { sessionGate.Release(); }
    }

    private async Task<T> Locked<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        await sessionGate.WaitAsync(cancellationToken);
        try { return await action(); }
        finally { sessionGate.Release(); }
    }

    private async Task Locked(Action action, CancellationToken cancellationToken)
    {
        await sessionGate.WaitAsync(cancellationToken);
        try { await Task.Run(action, cancellationToken); }
        finally { sessionGate.Release(); }
    }

    private sealed record BrowserSession(Guid TestRunId, string ProfilePath, ChromeDriverService Service, ChromeDriver Driver);
    private sealed record BrowserElementReference(string Ref, string CssSelector, string PageUrl);
}
