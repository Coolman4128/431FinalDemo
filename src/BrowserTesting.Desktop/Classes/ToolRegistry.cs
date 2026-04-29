using System.Text.Json.Nodes;
using BrowserTesting.Desktop.Models;

namespace BrowserTesting.Desktop.Classes;

public sealed class ToolRegistry
{
    private readonly IReadOnlyList<LlmToolDefinition> definitions =
    [
        Define("open_browser", "Open a Chrome browser for the active run.", Object(Property("url", "string"), Property("profile_name", "string"))),
        Define("close_browser", "Close the browser for the active run.", Object()),
        Define("list_tabs", "List open tabs for the active browser.", Object()),
        Define("switch_tab", "Switch to a tab by index or handle.", Object(Property("index", "integer"), Property("handle", "string"))),
        Define("goto_url", "Navigate the browser to a URL.", Object(Property("url", "string", true))),
        Define("back", "Navigate backward in browser history.", Object()),
        Define("forward", "Navigate forward in browser history.", Object()),
        Define("refresh", "Refresh the current page.", Object()),
        Define("get_page_state", "Return the current URL, title, and tab summary.", Object()),
        Define("find_element", LocatorDescription("Find the first matching element"), LocatorSchema("locator")),
        Define("find_elements", LocatorDescription("Find all matching elements"), LocatorSchema("locator")),
        Define("inspect_page", "Return visible page text plus a compact list of visible actionable elements with page-local refs for click_ref/type_ref. Use before guessing selectors.", Object(Property("max_elements", "integer"), Property("include_hidden", "boolean"))),
        Define("click_ref", "Click an element ref returned by the latest inspect_page for the current page URL.", Object(Property("ref", "string", true))),
        Define("type_ref", "Type text into an element ref returned by the latest inspect_page for the current page URL.", Object(Property("ref", "string", true), Property("text", "string", true), Property("clear_first", "boolean"))),
        Define("click", LocatorDescription("Click an element"), LocatorSchema("locator")),
        Define("double_click", LocatorDescription("Double click an element"), LocatorSchema("locator")),
        Define("type_text", "Type text into an element. Use {\"locator\":{\"strategy\":\"...\",\"value\":\"...\"},\"text\":\"...\"}.", LocatorSchema("locator", Property("text", "string", true), Property("clear_first", "boolean"))),
        Define("clear", LocatorDescription("Clear the value of an input element"), LocatorSchema("locator")),
        Define("send_keys", "Send raw keys to an element. Use {\"locator\":{\"strategy\":\"...\",\"value\":\"...\"},\"keys\":\"...\"}.", LocatorSchema("locator", Property("keys", "string", true))),
        Define("submit", LocatorDescription("Submit a form element"), LocatorSchema("locator")),
        Define("select_option", "Select an option in a select element by text or value. Use {\"locator\":{\"strategy\":\"...\",\"value\":\"...\"},\"text\":\"...\"} or \"value\".", LocatorSchema("locator", Property("text", "string"), Property("value", "string"))),
        Define("hover", LocatorDescription("Move the mouse over an element"), LocatorSchema("locator")),
        Define("scroll_into_view", LocatorDescription("Scroll until the element is visible"), LocatorSchema("locator")),
        Define("get_text", LocatorDescription("Read text from an element"), LocatorSchema("locator")),
        Define("get_attribute", "Read an attribute from an element. Use {\"locator\":{\"strategy\":\"...\",\"value\":\"...\"},\"attribute\":\"...\"}.", LocatorSchema("locator", Property("attribute", "string", true))),
        Define("get_html", "Return bounded outer HTML from an optional element locator, or bounded page HTML when no locator is supplied. Prefer inspect_page for compact actionable refs.", OptionalLocatorSchema("locator")),
        Define("take_screenshot", "Capture a screenshot to disk.", Object(Property("name", "string"))),
        Define("execute_javascript", "Run JavaScript in the current page. Optional arguments must be JSON primitive values passed positionally.", Object(
            Property("script", "string", true),
            ArrayProperty("arguments", PrimitiveValueSchema()))),
        Define("wait_for_element", "Wait until an element exists. Use {\"locator\":{\"strategy\":\"...\",\"value\":\"...\"},\"timeout_ms\":5000}.", LocatorSchema("locator", Property("timeout_ms", "integer"))),
        Define("wait_for_text", "Wait until page source contains text.", Object(Property("text", "string", true), Property("timeout_ms", "integer"))),
        Define("wait_for_navigation", "Wait until the URL contains expected text.", Object(Property("url_contains", "string", true), Property("timeout_ms", "integer"))),
        Define("sleep", "Pause execution briefly.", Object(Property("milliseconds", "integer", true))),
        Define("get_cookies", "Return all cookies from the current page.", Object()),
        Define("set_cookie", "Set a cookie in the current browser.", Object(Property("name", "string", true), Property("value", "string", true), Property("domain", "string"), Property("path", "string"))),
        Define("read_local_storage", "Read local storage value by key.", Object(Property("key", "string", true))),
        Define("write_local_storage", "Write local storage value by key.", Object(Property("key", "string", true), Property("value", "string", true))),
        Define("create_goal", "Create a new test goal for the active run.", Object(Property("title", "string", true), Property("success_criteria", "string", true))),
        Define("update_goal_status", "Update a goal status to pending, running, passed, or failed.", Object(Property("goal_id", "string", true), Property("status", "string", true), Property("note", "string"), Property("evidence", "string"))),
        Define("mark_goal_pass", "Mark a goal as passed with evidence.", Object(Property("goal_id", "string", true), Property("evidence", "string", true))),
        Define("mark_goal_fail", "Mark a goal as failed with reason and evidence.", Object(Property("goal_id", "string", true), Property("reason", "string", true), Property("evidence", "string"))),
        Define("list_goals", "List all goals for the active run.", Object()),
        Define("end_task", "Finish the active run after every goal is passed or failed. Include final text summarizing what was done and the test results. Use only when all active-run goals are terminal.", Object(
            EnumProperty("outcome", ["completed", "failed"], true),
            Property("summary", "string", true, "One or two paragraphs, at least 120 characters, summarizing what you did during the run. Do not use a terse phrase."),
            Property("test_results", "string", true, "One or two paragraphs, at least 120 characters, summarizing which tests or goals passed or failed and why. Do not use a terse phrase."),
            Property("evidence", "string", true),
            Property("remaining_work", "string", true))),
        Define("save_secret", "Save a named secret for this chat.", Object(Property("name", "string", true), Property("value", "string", true))),
        Define("get_secret", "Retrieve a named secret for this chat.", Object(Property("name", "string", true))),
        Define("list_secrets", "List saved secret names for this chat.", Object()),
    ];

    public IReadOnlyList<LlmToolDefinition> GetToolDefinitions() => definitions;

    private static LlmToolDefinition Define(string name, string description, JsonObject parameters) =>
        new()
        {
            Name = name,
            Description = description,
            Parameters = parameters,
        };

    private static JsonObject LocatorSchema(string locatorName, params JsonObject[] extraProperties) =>
        LocatorSchema(locatorName, locatorRequired: true, extraProperties);

    private static JsonObject OptionalLocatorSchema(string locatorName, params JsonObject[] extraProperties) =>
        LocatorSchema(locatorName, locatorRequired: false, extraProperties);

    private static JsonObject LocatorSchema(string locatorName, bool locatorRequired, params JsonObject[] extraProperties)
    {
        var allProperties = new List<JsonObject>
        {
            new()
            {
                ["name"] = locatorName,
                ["required"] = locatorRequired,
                ["schema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["strategy"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["enum"] = new JsonArray("css", "xpath", "id", "name", "class", "tag", "link_text", "partial_link_text"),
                        },
                        ["value"] = new JsonObject
                        {
                            ["type"] = "string",
                        },
                    },
                    ["required"] = new JsonArray("strategy", "value"),
                },
            },
        };

        allProperties.AddRange(extraProperties);
        return Object(allProperties.ToArray());
    }

    private static string LocatorDescription(string action) =>
        $"{action} using a required locator argument shaped like {{\"locator\":{{\"strategy\":\"css\",\"value\":\"selector\"}}}}.";

    private static JsonObject Object(params JsonObject[] properties)
    {
        var objectProperties = new JsonObject();
        var required = new JsonArray();

        foreach (var property in properties)
        {
            var name = property["name"]!.GetValue<string>();
            objectProperties[name] = property["schema"]!.DeepClone();
            if (property["required"]?.GetValue<bool>() == true)
            {
                required.Add(name);
            }
        }

        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = objectProperties,
        };

        if (required.Count > 0)
        {
            schema["required"] = required;
        }

        return schema;
    }

    private static JsonObject Property(string name, string type, bool required = false, string? description = null)
    {
        var schema = new JsonObject
        {
            ["type"] = type,
        };

        if (!string.IsNullOrWhiteSpace(description))
        {
            schema["description"] = description;
        }

        return new JsonObject
        {
            ["name"] = name,
            ["required"] = required,
            ["schema"] = schema,
        };
    }

    private static JsonObject EnumProperty(string name, string[] choices, bool required = false) =>
        new()
        {
            ["name"] = name,
            ["required"] = required,
            ["schema"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray(choices.Select(choice => JsonValue.Create(choice)).ToArray()),
            },
        };

    private static JsonObject ArrayProperty(string name, JsonObject itemSchema, bool required = false) =>
        new()
        {
            ["name"] = name,
            ["required"] = required,
            ["schema"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = itemSchema.DeepClone(),
            },
        };

    private static JsonObject PrimitiveValueSchema() =>
        new()
        {
            ["anyOf"] = new JsonArray
            {
                new JsonObject { ["type"] = "string" },
                new JsonObject { ["type"] = "number" },
                new JsonObject { ["type"] = "integer" },
                new JsonObject { ["type"] = "boolean" },
                new JsonObject { ["type"] = "null" },
            },
        };
}
