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
            var snapshot = await OpenBrowserAsync(testRunId, GetString(arguments, "url"), profilePath, headless, cancellationToken);
            return ToolExecutionResult.Successful("Chrome opened.", SnapshotNode(snapshot));
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
                return toolName switch
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
            return ToolExecutionResult.Successful($"Found {elements.Count} elements.", new JsonArray(elements.Select(DescribeElement).ToArray()));
        }

        var element = driver.FindElement(GetBy(arguments));
        return ToolExecutionResult.Successful("Element found.", DescribeElement(element));
    }

    private static ToolExecutionResult Interact(IWebDriver driver, JsonObject arguments, Action<IWebElement> action, string summary)
    {
        var element = driver.FindElement(GetBy(arguments));
        action(element);
        return ToolExecutionResult.Successful(summary, DescribeElement(element));
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
        return ToolExecutionResult.Successful("Text entered.", new JsonObject { ["text"] = text });
    }

    private static ToolExecutionResult SendKeys(IWebDriver driver, JsonObject arguments)
    {
        var keys = GetString(arguments, "keys") ?? string.Empty;
        var element = driver.FindElement(GetBy(arguments));
        element.SendKeys(keys);
        return ToolExecutionResult.Successful("Keys sent.", new JsonObject { ["keys"] = keys });
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
        return ToolExecutionResult.Successful("Element text captured.", new JsonObject { ["text"] = element.Text });
    }

    private static ToolExecutionResult ReadAttribute(IWebDriver driver, JsonObject arguments)
    {
        var attribute = GetString(arguments, "attribute") ?? "value";
        var element = driver.FindElement(GetBy(arguments));
        return ToolExecutionResult.Successful("Attribute read.", new JsonObject
        {
            ["attribute"] = attribute,
            ["value"] = element.GetAttribute(attribute),
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
        return ToolExecutionResult.Successful("HTML captured.", new JsonObject { ["html"] = html });
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
        return ToolExecutionResult.Successful("JavaScript executed.", new JsonObject { ["result"] = result?.ToString() });
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
                return ToolExecutionResult.Successful("Text was found on the page.", new JsonObject { ["text"] = text });
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
                return ToolExecutionResult.Successful("Navigation condition satisfied.", new JsonObject { ["url"] = driver.Url });
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
            ["tag"] = element.TagName,
            ["text"] = element.Text,
            ["displayed"] = element.Displayed,
            ["enabled"] = element.Enabled,
            ["id"] = element.GetAttribute("id"),
            ["class"] = element.GetAttribute("class"),
            ["name"] = element.GetAttribute("name"),
        };

    private static JsonObject SnapshotNode(BrowserSessionSnapshot snapshot)
    {
        var tabs = new JsonArray();
        foreach (var tab in snapshot.Tabs)
        {
            tabs.Add(new JsonObject
            {
                ["handle"] = tab.Handle,
                ["title"] = tab.Title,
                ["url"] = tab.Url,
                ["is_selected"] = tab.IsSelected,
            });
        }

        return new JsonObject
        {
            ["current_url"] = snapshot.CurrentUrl,
            ["page_title"] = snapshot.PageTitle,
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

    private static string? SafeGet(Func<string> getter)
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
}
