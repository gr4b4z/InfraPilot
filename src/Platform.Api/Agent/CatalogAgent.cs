using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Platform.Api.Features.Catalog;

namespace Platform.Api.Agent;

public class CatalogAgent
{
    private readonly CatalogYamlLoader _catalogLoader;
    private readonly A2UIFormGenerator _formGenerator;
    private readonly ValidationRunner _validationRunner;
    private readonly PlatformQueryService _queryService;
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CatalogAgent> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private const string SystemPrompt = """
        You are a service catalog assistant for a platform engineering team.
        You help users request infrastructure services like repository creation, pipeline runs, and access management.
        You can also answer questions about recent requests, deployments, approvals, and platform activity.

        When a user selects a service or describes what they need:
        1. Identify the matching catalog item using SearchCatalog
        2. Call GenerateForm to render the request form
        3. Wait for the user to fill the form and click Validate
        4. When you receive form data, call ValidateRequest
        5. For failed validations: explain what's wrong, suggest corrections
        6. When everything is valid, show a ReviewCard summary and confirm submission

        When a user asks about service requests (catalog requests, approvals, etc.):
        - Use query_requests to find specific service requests or list recent ones
        - Use get_request_timeline to show the audit trail for a specific request
        - Use get_summary to show aggregate stats for service requests in a date range

        When a user asks about deployments, releases, what's deployed, what version is running, what was deployed to production, what changed recently, etc.:
        - ALWAYS use the deployment tools (list_products, get_deployment_state, query_deployments) — NEVER say you don't have access to deployment data
        - Use list_products first if you don't know which products exist
        - Use get_deployment_state to show the current version matrix for a product
        - Use query_deployments to show recent deployment activity (what was deployed today, what changed in production, etc.)
        - The system will render rich data cards for deployment results
        - Include navigation links in your response so the user can open the full deployment view. URL format:
          - State matrix: /deployments/{product}
          - Activity view: /deployments/{product}?tab=activity
          - Activity with time filter: /deployments/{product}?tab=activity&atime=today
          - Activity with environment: /deployments/{product}?tab=activity&env=production
          - Combined filters: /deployments/{product}?tab=activity&atime=24h&env=staging

        Rules:
        - Always respond in the same language the user uses
        - Be concise
        - When suggesting field corrections, explain WHY briefly
        - Never make up validation results
        - When returning query results, summarize them conversationally and the system will render data cards
        - When showing deployment data, always mention the navigation link so the user can explore further
        """;

    // Azure OpenAI tool definitions for function calling
    private static readonly object[] ToolDefinitions =
    [
        new
        {
            type = "function",
            function = new
            {
                name = "query_requests",
                description = "Search and filter service requests (catalog requests like creating repos, namespaces, DNS records, etc.). Do NOT use this for deployment/release questions — use query_deployments instead.",
                parameters = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["status"] = new { type = "string", description = "Filter by status: Draft, Validating, AwaitingApproval, Executing, Completed, Failed, Rejected, etc." },
                        ["requester"] = new { type = "string", description = "Filter by requester name (partial match)" },
                        ["catalog_slug"] = new { type = "string", description = "Filter by catalog service slug, e.g. create-repo, create-namespace" },
                        ["from"] = new { type = "string", description = "Start date ISO8601 (e.g. 2026-04-11T00:00:00Z)" },
                        ["to"] = new { type = "string", description = "End date ISO8601 (e.g. 2026-04-11T23:59:59Z)" },
                        ["search"] = new { type = "string", description = "Free-text search across requester name and service name" },
                    },
                    required = Array.Empty<string>(),
                },
            },
        },
        new
        {
            type = "function",
            function = new
            {
                name = "get_request_timeline",
                description = "Get the audit trail / timeline for a specific request. Shows who did what and when.",
                parameters = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["request_id"] = new { type = "string", description = "The GUID of the request" },
                    },
                    required = new[] { "request_id" },
                },
            },
        },
        new
        {
            type = "function",
            function = new
            {
                name = "get_summary",
                description = "Get aggregate stats for service requests (catalog requests) in a date range. Use for questions like 'how many requests this week' or 'summary of today's requests'. NOT for deployment questions.",
                parameters = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["from"] = new { type = "string", description = "Start date ISO8601" },
                        ["to"] = new { type = "string", description = "End date ISO8601" },
                    },
                    required = new[] { "from", "to" },
                },
            },
        },
        new
        {
            type = "function",
            function = new
            {
                name = "get_deployment_state",
                description = "Get the current deployment state matrix for a product — shows latest version of every service in every environment. Use when users ask 'what is deployed', 'current versions', 'what's in production', etc.",
                parameters = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["product"] = new { type = "string", description = "Product name, e.g. 'billing-platform'. Call list_products first if unknown." },
                    },
                    required = new[] { "product" },
                },
            },
        },
        new
        {
            type = "function",
            function = new
            {
                name = "query_deployments",
                description = "Query recent deployment activity across all products and environments. ALWAYS use this tool when users ask about deployments, releases, versions, what was deployed, what changed in production/staging, etc. Returns deployment events with version changes, work items, participants, and PR links.",
                parameters = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["product"] = new { type = "string", description = "Product name, e.g. 'billing-platform'. Optional — omit to query across all products." },
                        ["environment"] = new { type = "string", description = "Environment name, e.g. 'production', 'staging'. Optional." },
                        ["since"] = new { type = "string", description = "ISO8601 datetime — only return deployments after this time. Defaults to start of today if omitted." },
                    },
                    required = Array.Empty<string>(),
                },
            },
        },
        new
        {
            type = "function",
            function = new
            {
                name = "list_products",
                description = "List all known products that have deployment data. Use this to discover available products before querying deployments.",
                parameters = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>(),
                    required = Array.Empty<string>(),
                },
            },
        },
    ];

    public CatalogAgent(
        CatalogYamlLoader catalogLoader,
        A2UIFormGenerator formGenerator,
        ValidationRunner validationRunner,
        PlatformQueryService queryService,
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<CatalogAgent> logger)
    {
        _catalogLoader = catalogLoader;
        _formGenerator = formGenerator;
        _validationRunner = validationRunner;
        _queryService = queryService;
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<CatalogAgentResponse> HandleAsync(CatalogAgentRequest request)
    {
        var history = request.History ?? [];

        // Route 1: catalogSlug provided, no formData -> generate form
        if (!string.IsNullOrWhiteSpace(request.CatalogSlug) && request.FormData is null)
        {
            return HandleGenerateForm(request.CatalogSlug, request.Message);
        }

        // Route 2: explicit validate action + formData -> run validation
        if (request.Action == "validate" && request.FormData is not null && !string.IsNullOrWhiteSpace(request.CatalogSlug))
        {
            return await HandleValidation(request.CatalogSlug, request.FormData, request.Message);
        }

        // Route 3: on a form page with context -> form-aware chat (help filling fields)
        if (!string.IsNullOrWhiteSpace(request.CatalogSlug) && request.FormData is not null && !string.IsNullOrWhiteSpace(request.Message))
        {
            return await HandleFormChat(request.CatalogSlug, request.FormData, request.Message, history);
        }

        // Route 4: conversational chat with full history + function calling
        return await HandleChat(request.Message, history);
    }

    private CatalogAgentResponse HandleGenerateForm(string catalogSlug, string? userMessage)
    {
        var definition = _catalogLoader.LoadAll().FirstOrDefault(d => d.Id == catalogSlug);
        if (definition is null)
        {
            return new CatalogAgentResponse
            {
                Reply = $"I couldn't find a catalog item with ID '{catalogSlug}'. Please check the service name and try again.",
            };
        }

        var formJson = _formGenerator.Generate(definition);

        return new CatalogAgentResponse
        {
            Reply = $"Here is the request form for **{definition.Name}**. Please fill in the required fields and click Validate when ready.",
            A2uiSurface = formJson,
        };
    }

    private async Task<CatalogAgentResponse> HandleValidation(
        string catalogSlug,
        Dictionary<string, JsonElement> formData,
        string? userMessage)
    {
        var definition = _catalogLoader.LoadAll().FirstOrDefault(d => d.Id == catalogSlug);
        if (definition is null)
        {
            return new CatalogAgentResponse
            {
                Reply = $"I couldn't find a catalog item with ID '{catalogSlug}'. Unable to validate.",
            };
        }

        var converted = new Dictionary<string, object?>();
        foreach (var (key, value) in formData)
        {
            converted[key] = ConvertJsonElement(value);
        }

        var validationResult = await _validationRunner.Validate(definition, converted);

        string reply;
        if (validationResult.IsValid)
        {
            var summary = BuildReviewCard(definition, converted);
            reply = $"All validations passed. Here is your request summary:\n\n{summary}\n\nShall I submit this request?";
        }
        else
        {
            var failures = validationResult.Results
                .Where(r => !r.Passed)
                .Select(r => $"- **{r.FieldId}**: {r.Message}");
            reply = $"Some validations failed. Please correct the following:\n\n{string.Join("\n", failures)}";
        }

        return new CatalogAgentResponse
        {
            Reply = reply,
            ValidationResults = validationResult,
        };
    }

    /// <summary>
    /// Form-aware chat: the user is on a form page and asking for help with specific fields.
    /// The agent can see all field definitions + current values and suggest/fill fields.
    /// </summary>
    private async Task<CatalogAgentResponse> HandleFormChat(
        string catalogSlug,
        Dictionary<string, JsonElement> formData,
        string userMessage,
        List<HistoryMessage> history)
    {
        var definition = _catalogLoader.LoadAll().FirstOrDefault(d => d.Id == catalogSlug);
        if (definition is null)
        {
            return new CatalogAgentResponse
            {
                Reply = $"I couldn't find a catalog item with ID '{catalogSlug}'.",
            };
        }

        // Build field context: definitions + current values
        var fieldContext = new StringBuilder();
        var converted = new Dictionary<string, object?>();
        foreach (var (key, value) in formData)
        {
            converted[key] = ConvertJsonElement(value);
        }

        foreach (var input in definition.Inputs)
        {
            converted.TryGetValue(input.Id, out var currentValue);
            var valueStr = currentValue?.ToString() ?? "(empty)";
            var optionsStr = input.Options?.Count > 0
                ? $" [options: {string.Join(", ", input.Options.Select(o => $"{o.Id}={o.Label}"))}]"
                : "";
            var requiredStr = input.Required ? " (REQUIRED)" : "";
            var validationStr = !string.IsNullOrWhiteSpace(input.Validation) ? $" [validation: {input.Validation}]" : "";

            fieldContext.AppendLine($"- **{input.Label}** (id: `{input.Id}`, type: {input.Component}){requiredStr}{optionsStr}{validationStr}");
            fieldContext.AppendLine($"  Current value: {valueStr}");
        }

        var formSystemPrompt = $"""
            You are a helpful assistant guiding the user through filling out a service request form.
            You have DIRECT ACCESS to update form fields via the `fill_fields` tool. When you call it, the form is updated instantly on the user's screen.

            Service: **{definition.Name}**
            Description: {definition.Description}
            Category: {definition.Category}

            Form fields and current values:
            {fieldContext}

            CRITICAL RULES FOR FILLING FIELDS:
            1. When the user provides a value (e.g. "set it to X", "use 10.13.1.10", "I enter X", "put X there"), you MUST call `fill_fields` immediately. Do NOT tell the user to enter it manually — you can do it for them.
            2. When the user asks what to put in a field, explain briefly then call `fill_fields` with your suggested value.
            3. When the user says "fill everything" or "fill the form", determine reasonable values for all fields you can and call `fill_fields`.
            4. After calling `fill_fields`, confirm what you filled in a brief message like "Done! I've set [field] to [value]."
            5. NEVER say "I'm unable to update the field" or "please enter it manually" — you CAN update fields, always use the tool.

            Other rules:
            - Always respond in the same language the user uses
            - Be concise
            - For select/multi-select fields, only use values from the options list
            - Respect validation patterns when suggesting values
            - If you truly can't determine what value the user wants, ask — but if they gave you the value, just fill it
            """;

        // Build tool definition dynamically from actual catalog fields
        // Each field becomes an explicit parameter so the LLM sees them clearly
        var fieldProperties = new Dictionary<string, object>();
        foreach (var input in definition.Inputs)
        {
            var propType = input.Component switch
            {
                "NumberInput" => "number",
                "Toggle" => "boolean",
                _ => "string",
            };

            var desc = input.Label;
            if (input.Options is { Count: > 0 })
                desc += $" [allowed values: {string.Join(", ", input.Options.Select(o => o.Id))}]";
            if (!string.IsNullOrWhiteSpace(input.Placeholder))
                desc += $" (e.g. {input.Placeholder})";
            if (!string.IsNullOrWhiteSpace(input.Validation))
                desc += $" [pattern: {input.Validation}]";

            fieldProperties[input.Id] = new { type = propType, description = desc };
        }

        var formTools = new object[]
        {
            new
            {
                type = "function",
                function = new
                {
                    name = "fill_fields",
                    description = "Set one or more field values in the request form. Call this to fill or update any fields for the user. Only include the fields you want to set.",
                    parameters = new
                    {
                        type = "object",
                        properties = fieldProperties,
                    },
                },
            },
        };

        // Call Azure OpenAI with form-aware tools
        var (reply, fieldSuggestions) = await CallWithFormTools(userMessage, formSystemPrompt, history, formTools, definition);

        return new CatalogAgentResponse
        {
            Reply = reply,
            FieldSuggestions = fieldSuggestions,
        };
    }

    /// <summary>
    /// Azure OpenAI call with form field-filling tool support.
    /// Returns the text reply and any field values the LLM chose to fill.
    /// </summary>
    private async Task<(string Reply, Dictionary<string, object>? FieldSuggestions)> CallWithFormTools(
        string userMessage, string systemPrompt, List<HistoryMessage> history,
        object[] tools, CatalogDefinition definition)
    {
        var endpoint = _configuration["AzureOpenAI:Endpoint"]!;
        var apiKey = _configuration["AzureOpenAI:ApiKey"]!;
        var deploymentName = _configuration["AzureOpenAI:DeploymentName"]!;
        var url = $"{endpoint.TrimEnd('/')}/openai/deployments/{deploymentName}/chat/completions?api-version=2024-10-21";

        var messages = new List<object> { new { role = "system", content = systemPrompt } };
        foreach (var h in history)
        {
            if (!string.IsNullOrWhiteSpace(h.Content))
                messages.Add(new { role = h.Role, content = h.Content });
        }
        var lastHistory = history.LastOrDefault();
        if (lastHistory is null || lastHistory.Content != userMessage)
            messages.Add(new { role = "user", content = userMessage });

        Dictionary<string, object>? allSuggestions = null;

        for (var iteration = 0; iteration < 3; iteration++)
        {
            var body = new { messages, tools, temperature = 0.3, max_tokens = 1024 };
            var json = JsonSerializer.Serialize(body, JsonOptions);
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
            httpRequest.Content = new StringContent(json, Encoding.UTF8, "application/json");
            httpRequest.Headers.Add("api-key", apiKey);

            try
            {
                using var httpResponse = await _httpClient.SendAsync(httpRequest);
                var responseBody = await httpResponse.Content.ReadAsStringAsync();

                if (!httpResponse.IsSuccessStatusCode)
                {
                    _logger.LogError("Azure OpenAI returned {StatusCode}: {Body}", httpResponse.StatusCode, responseBody);
                    return ("I'm having trouble connecting to the AI service. Please try again.", null);
                }

                var responseDoc = JsonDocument.Parse(responseBody);
                var choice = responseDoc.RootElement.GetProperty("choices")[0];
                var message = choice.GetProperty("message");
                var finishReason = choice.GetProperty("finish_reason").GetString();

                if (finishReason == "tool_calls" && message.TryGetProperty("tool_calls", out var toolCalls))
                {
                    messages.Add(JsonSerializer.Deserialize<object>(message.GetRawText(), JsonOptions)!);

                    foreach (var toolCall in toolCalls.EnumerateArray())
                    {
                        var toolId = toolCall.GetProperty("id").GetString()!;
                        var functionName = toolCall.GetProperty("function").GetProperty("name").GetString()!;
                        var arguments = toolCall.GetProperty("function").GetProperty("arguments").GetString()!;

                        if (functionName == "fill_fields")
                        {
                            // Fields are now top-level params (e.g. {"hostname": "aaa.wp.pl", "ttl": 300})
                            var args = JsonDocument.Parse(arguments).RootElement;
                            allSuggestions ??= new Dictionary<string, object>();
                            var validFields = definition.Inputs.Select(i => i.Id).ToHashSet();

                            foreach (var prop in args.EnumerateObject())
                            {
                                if (validFields.Contains(prop.Name))
                                {
                                    allSuggestions[prop.Name] = prop.Value.ValueKind switch
                                    {
                                        JsonValueKind.String => prop.Value.GetString()!,
                                        JsonValueKind.Number => prop.Value.TryGetInt64(out var l) ? l : prop.Value.GetDouble(),
                                        JsonValueKind.True => true,
                                        JsonValueKind.False => false,
                                        _ => prop.Value.GetRawText(),
                                    };
                                }
                            }

                            var updatedSummary = allSuggestions.Count > 0
                                ? string.Join(", ", allSuggestions.Select(kvp => $"{kvp.Key} = \"{kvp.Value}\""))
                                : "none";

                            messages.Add(new
                            {
                                role = "tool",
                                tool_call_id = toolId,
                                content = allSuggestions.Count > 0
                                    ? $"SUCCESS: Form fields updated on the user's screen: {updatedSummary}. Tell the user what you filled."
                                    : "No valid fields matched. Check field IDs and try again.",
                            });
                        }
                        else
                        {
                            messages.Add(new { role = "tool", tool_call_id = toolId, content = "Unknown tool" });
                        }
                    }
                    continue;
                }

                var content = message.TryGetProperty("content", out var contentProp) ? contentProp.GetString() ?? "" : "";
                return (content, allSuggestions);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to call Azure OpenAI for form chat");
                return ("I encountered an error. Please try again.", null);
            }
        }

        return ("I've reached the maximum number of steps. Please try rephrasing.", allSuggestions);
    }

    private async Task<CatalogAgentResponse> HandleChat(string? userMessage, List<HistoryMessage> history)
    {
        if (string.IsNullOrWhiteSpace(userMessage))
        {
            return new CatalogAgentResponse
            {
                Reply = "Hello! I'm your service catalog assistant. I can help you request infrastructure services or answer questions about recent deployments and requests. What would you like to do?",
            };
        }

        var catalogItems = _catalogLoader.LoadAll();
        var catalogContext = BuildCatalogContext(catalogItems);

        var systemPrompt = $"""
            {SystemPrompt}

            Available catalog items:
            {catalogContext}

            Today's date is {DateTimeOffset.UtcNow:yyyy-MM-dd}.

            IMPORTANT: If the user's request matches one of the catalog items above, you MUST include this exact tag at the END of your reply:
            [SERVICE:slug-here]

            For example, if the user wants a repository, end with [SERVICE:create-repo]
            If the user wants DNS changes, end with [SERVICE:request-dns-record]
            If the user is just asking a general question or querying data, do NOT include the tag.
            """;

        // Call Azure OpenAI with function calling loop
        var (reply, cards) = await CallWithFunctionCalling(userMessage, systemPrompt, history);

        // Extract [SERVICE:slug] tag from reply
        string? suggestedSlug = null;
        Dictionary<string, object>? fieldSuggestions = null;
        var tagMatch = System.Text.RegularExpressions.Regex.Match(reply, @"\[SERVICE:([a-z0-9-]+)\]");
        if (tagMatch.Success)
        {
            suggestedSlug = tagMatch.Groups[1].Value;
            if (!catalogItems.Any(c => c.Id == suggestedSlug))
            {
                suggestedSlug = null;
            }
            reply = reply.Replace(tagMatch.Value, "").Trim();

            // Phase 2: Extract field suggestions from conversation when service is identified
            if (suggestedSlug is not null)
            {
                fieldSuggestions = await ExtractFieldSuggestions(suggestedSlug, userMessage, history);
            }
        }

        return new CatalogAgentResponse
        {
            Reply = reply,
            SuggestedSlug = suggestedSlug,
            FieldSuggestions = fieldSuggestions,
            Cards = cards.Count > 0 ? cards : null,
        };
    }

    /// <summary>
    /// Azure OpenAI function calling loop: send messages, check for tool_calls,
    /// execute tools, send results back, repeat until we get a final text response.
    /// </summary>
    private async Task<(string Reply, List<AgentCard> Cards)> CallWithFunctionCalling(
        string userMessage, string systemPromptOverride, List<HistoryMessage>? history)
    {
        var endpoint = _configuration["AzureOpenAI:Endpoint"]
            ?? throw new InvalidOperationException("AzureOpenAI:Endpoint is not configured");
        var apiKey = _configuration["AzureOpenAI:ApiKey"]
            ?? throw new InvalidOperationException("AzureOpenAI:ApiKey is not configured");
        var deploymentName = _configuration["AzureOpenAI:DeploymentName"]
            ?? throw new InvalidOperationException("AzureOpenAI:DeploymentName is not configured");

        var url = $"{endpoint.TrimEnd('/')}/openai/deployments/{deploymentName}/chat/completions?api-version=2024-10-21";

        // Build messages array
        var messages = new List<object> { new { role = "system", content = systemPromptOverride } };

        if (history is not null)
        {
            foreach (var h in history)
            {
                if (!string.IsNullOrWhiteSpace(h.Content))
                    messages.Add(new { role = h.Role, content = h.Content });
            }
        }

        var lastHistory = history?.LastOrDefault();
        if (lastHistory is null || lastHistory.Content != userMessage)
        {
            messages.Add(new { role = "user", content = userMessage });
        }

        var cards = new List<AgentCard>();
        const int maxIterations = 5; // safety limit

        for (var iteration = 0; iteration < maxIterations; iteration++)
        {
            var body = new
            {
                messages,
                tools = ToolDefinitions,
                temperature = 0.3,
                max_tokens = 1024,
            };

            var json = JsonSerializer.Serialize(body, JsonOptions);
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
            httpRequest.Content = new StringContent(json, Encoding.UTF8, "application/json");
            httpRequest.Headers.Add("api-key", apiKey);

            try
            {
                using var httpResponse = await _httpClient.SendAsync(httpRequest);
                var responseBody = await httpResponse.Content.ReadAsStringAsync();

                if (!httpResponse.IsSuccessStatusCode)
                {
                    _logger.LogError("Azure OpenAI returned {StatusCode}: {Body}", httpResponse.StatusCode, responseBody);
                    return ("I'm sorry, I'm having trouble connecting to the AI service right now. Please try again later.", cards);
                }

                var responseDoc = JsonDocument.Parse(responseBody);
                var choice = responseDoc.RootElement.GetProperty("choices")[0];
                var message = choice.GetProperty("message");
                var finishReason = choice.GetProperty("finish_reason").GetString();

                // If finish_reason is "tool_calls", execute the tools and loop
                if (finishReason == "tool_calls" && message.TryGetProperty("tool_calls", out var toolCalls))
                {
                    // Add the assistant's tool_calls message to history
                    messages.Add(JsonSerializer.Deserialize<object>(message.GetRawText(), JsonOptions)!);

                    foreach (var toolCall in toolCalls.EnumerateArray())
                    {
                        var toolId = toolCall.GetProperty("id").GetString()!;
                        var functionName = toolCall.GetProperty("function").GetProperty("name").GetString()!;
                        var arguments = toolCall.GetProperty("function").GetProperty("arguments").GetString()!;

                        _logger.LogInformation("Agent calling tool: {Tool} with args: {Args}", functionName, arguments);

                        var (toolResult, card) = await ExecuteTool(functionName, arguments);

                        if (card is not null)
                            cards.Add(card);

                        // Add tool result to messages
                        messages.Add(new
                        {
                            role = "tool",
                            tool_call_id = toolId,
                            content = toolResult,
                        });
                    }

                    continue; // loop to get final response
                }

                // Normal text response — we're done
                var content = message.TryGetProperty("content", out var contentProp)
                    ? contentProp.GetString() ?? ""
                    : "";

                return (content, cards);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to call Azure OpenAI (iteration {Iteration})", iteration);
                return ("I'm sorry, I encountered an error while processing your request. Please try again later.", cards);
            }
        }

        return ("I've reached the maximum number of steps. Please try rephrasing your question.", cards);
    }

    /// <summary>
    /// Execute a tool call from Azure OpenAI and return (resultJson, optionalCard).
    /// </summary>
    private async Task<(string Result, AgentCard? Card)> ExecuteTool(string functionName, string arguments)
    {
        try
        {
            var args = JsonDocument.Parse(arguments).RootElement;

            switch (functionName)
            {
                case "query_requests":
                {
                    var status = args.TryGetProperty("status", out var s) ? s.GetString() : null;
                    var requester = args.TryGetProperty("requester", out var r) ? r.GetString() : null;
                    var catalogSlug = args.TryGetProperty("catalog_slug", out var cs) ? cs.GetString() : null;
                    var from = args.TryGetProperty("from", out var f) && DateTimeOffset.TryParse(f.GetString(), out var fd) ? fd : (DateTimeOffset?)null;
                    var to = args.TryGetProperty("to", out var t) && DateTimeOffset.TryParse(t.GetString(), out var td) ? td : (DateTimeOffset?)null;
                    var search = args.TryGetProperty("search", out var srch) ? srch.GetString() : null;

                    var results = await _queryService.QueryRequests(status, requester, catalogSlug, from, to, search);
                    var resultJson = JsonSerializer.Serialize(results, JsonOptions);

                    var card = new AgentCard
                    {
                        Type = "deployment-list",
                        Title = "Matching Requests",
                        Data = results,
                    };

                    return (resultJson, card);
                }

                case "get_request_timeline":
                {
                    var requestId = args.GetProperty("request_id").GetString()!;
                    if (!Guid.TryParse(requestId, out var id))
                        return ("Invalid request ID format", null);

                    var timeline = await _queryService.GetRequestTimeline(id);
                    var detail = await _queryService.GetRequestDetail(id);
                    var resultJson = JsonSerializer.Serialize(new { detail, timeline }, JsonOptions);

                    var cards = new List<AgentCard>();
                    if (detail is not null)
                    {
                        return (resultJson, new AgentCard
                        {
                            Type = "timeline",
                            Title = $"Timeline for {detail.ServiceName}",
                            Data = new { detail, timeline },
                        });
                    }

                    return (resultJson, new AgentCard
                    {
                        Type = "timeline",
                        Title = "Request Timeline",
                        Data = new { timeline },
                    });
                }

                case "get_summary":
                {
                    var from = DateTimeOffset.Parse(args.GetProperty("from").GetString()!);
                    var to = DateTimeOffset.Parse(args.GetProperty("to").GetString()!);

                    var summary = await _queryService.GetSummary(from, to);
                    var resultJson = JsonSerializer.Serialize(summary, JsonOptions);

                    return (resultJson, new AgentCard
                    {
                        Type = "summary",
                        Title = "Request Summary",
                        Data = summary,
                    });
                }

                case "get_deployment_state":
                {
                    var product = args.GetProperty("product").GetString()!;
                    var stateData = await _queryService.GetDeploymentState(product);
                    var resultJson = JsonSerializer.Serialize(stateData, JsonOptions);

                    return (resultJson, new AgentCard
                    {
                        Type = "deployment-state",
                        Title = $"Deployment State — {product}",
                        Data = stateData,
                    });
                }

                case "query_deployments":
                {
                    var product = args.TryGetProperty("product", out var p) ? p.GetString() : null;
                    var environment = args.TryGetProperty("environment", out var env) ? env.GetString() : null;
                    var since = args.TryGetProperty("since", out var sinceVal) && DateTimeOffset.TryParse(sinceVal.GetString(), out var sd)
                        ? sd
                        : DateTimeOffset.UtcNow.Date;

                    var activityData = await _queryService.GetRecentDeployments(product, environment, since, ct: default);
                    var resultJson = JsonSerializer.Serialize(activityData, JsonOptions);

                    return (resultJson, new AgentCard
                    {
                        Type = "deployment-activity",
                        Title = $"Recent Deployments{(product != null ? $" — {product}" : "")}",
                        Data = activityData,
                    });
                }

                case "list_products":
                {
                    var products = await _queryService.GetProducts();
                    var resultJson = JsonSerializer.Serialize(products, JsonOptions);
                    return (resultJson, null);
                }

                default:
                    return ($"Unknown tool: {functionName}", null);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute tool {Tool}", functionName);
            return ($"Error executing {functionName}: {ex.Message}", null);
        }
    }

    /// <summary>
    /// Phase 2: When a service is identified, extract field values from the conversation
    /// to pre-fill the form.
    /// </summary>
    private async Task<Dictionary<string, object>?> ExtractFieldSuggestions(
        string catalogSlug, string userMessage, List<HistoryMessage> history)
    {
        var definition = _catalogLoader.LoadAll().FirstOrDefault(d => d.Id == catalogSlug);
        if (definition is null || definition.Inputs.Count == 0) return null;

        var fieldDescriptions = string.Join("\n", definition.Inputs.Select(i =>
            $"- {i.Id} ({i.Component}): {i.Label}" + (i.Options?.Count > 0 ? $" [options: {string.Join(", ", i.Options.Select(o => o.Id))}]" : "")));

        var extractPrompt = $$"""
            Extract any field values that the user has mentioned in this conversation for the service "{{definition.Name}}".

            Available fields:
            {{fieldDescriptions}}

            Return ONLY a JSON object with field IDs as keys and extracted values.
            Only include fields where you're confident about the value from the conversation.
            If no values can be extracted, return {}.
            Do not include explanations, just the JSON object.
            """;

        // Build a minimal conversation context
        var conversationContext = new StringBuilder();
        if (history is not null)
        {
            foreach (var h in history.TakeLast(10))
            {
                conversationContext.AppendLine($"{h.Role}: {h.Content}");
            }
        }
        conversationContext.AppendLine($"user: {userMessage}");

        var reply = await CallAzureOpenAISimple($"{extractPrompt}\n\nConversation:\n{conversationContext}");

        try
        {
            // Try to parse as JSON
            var cleaned = reply.Trim();
            if (cleaned.StartsWith("```"))
            {
                cleaned = cleaned.Split('\n').Skip(1).TakeWhile(l => !l.StartsWith("```")).Aggregate((a, b) => a + "\n" + b);
            }

            var suggestions = JsonSerializer.Deserialize<Dictionary<string, object>>(cleaned);
            if (suggestions is not null && suggestions.Count > 0)
            {
                // Only keep fields that actually exist in the definition
                var validFields = definition.Inputs.Select(i => i.Id).ToHashSet();
                return suggestions
                    .Where(kvp => validFields.Contains(kvp.Key))
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse field extraction response: {Reply}", reply);
        }

        return null;
    }

    /// <summary>
    /// Simple single-shot call to Azure OpenAI (no function calling, no history).
    /// Used for field extraction.
    /// </summary>
    private async Task<string> CallAzureOpenAISimple(string prompt)
    {
        var endpoint = _configuration["AzureOpenAI:Endpoint"]!;
        var apiKey = _configuration["AzureOpenAI:ApiKey"]!;
        var deploymentName = _configuration["AzureOpenAI:DeploymentName"]!;
        var url = $"{endpoint.TrimEnd('/')}/openai/deployments/{deploymentName}/chat/completions?api-version=2024-10-21";

        var body = new
        {
            messages = new[] { new { role = "user", content = prompt } },
            temperature = 0.1,
            max_tokens = 512,
        };

        var json = JsonSerializer.Serialize(body, JsonOptions);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
        httpRequest.Content = new StringContent(json, Encoding.UTF8, "application/json");
        httpRequest.Headers.Add("api-key", apiKey);

        try
        {
            using var httpResponse = await _httpClient.SendAsync(httpRequest);
            var responseBody = await httpResponse.Content.ReadAsStringAsync();

            if (!httpResponse.IsSuccessStatusCode) return "{}";

            var responseDoc = JsonDocument.Parse(responseBody);
            return responseDoc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? "{}";
        }
        catch
        {
            return "{}";
        }
    }

    private static string BuildCatalogContext(List<CatalogDefinition> items)
    {
        var sb = new StringBuilder();
        foreach (var item in items)
        {
            sb.AppendLine($"- **{item.Name}** (slug: `{item.Id}`, category: {item.Category}): {item.Description}");
        }
        return sb.ToString();
    }

    private static string BuildReviewCard(CatalogDefinition definition, Dictionary<string, object?> formData)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"**Service:** {definition.Name}");
        sb.AppendLine($"**Category:** {definition.Category}");
        sb.AppendLine();
        sb.AppendLine("| Field | Value |");
        sb.AppendLine("|-------|-------|");

        foreach (var input in definition.Inputs)
        {
            formData.TryGetValue(input.Id, out var value);
            var display = value?.ToString() ?? "(not provided)";
            sb.AppendLine($"| {input.Label} | {display} |");
        }

        return sb.ToString();
    }

    private static object? ConvertJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Array => element.EnumerateArray().Select(ConvertJsonElement).ToList(),
            _ => element.GetRawText(),
        };
    }
}

public class CatalogAgentRequest
{
    public string? Message { get; set; }

    [JsonPropertyName("catalogSlug")]
    public string? CatalogSlug { get; set; }

    [JsonPropertyName("formData")]
    public Dictionary<string, JsonElement>? FormData { get; set; }

    [JsonPropertyName("threadId")]
    public string? ThreadId { get; set; }

    /// <summary>Last N messages for multi-turn context</summary>
    [JsonPropertyName("history")]
    public List<HistoryMessage>? History { get; set; }

    /// <summary>
    /// Explicit action: "validate" triggers validation, anything else is conversational.
    /// </summary>
    [JsonPropertyName("action")]
    public string? Action { get; set; }
}

public class HistoryMessage
{
    public string Role { get; set; } = "";
    public string Content { get; set; } = "";
}

public class CatalogAgentResponse
{
    public string Reply { get; set; } = "";

    [JsonPropertyName("a2uiSurface")]
    public string? A2uiSurface { get; set; }

    public object? ValidationResults { get; set; }

    /// <summary>
    /// When the agent identifies a matching service, this contains the slug
    /// so the frontend can offer a "Open request form" action.
    /// </summary>
    public string? SuggestedSlug { get; set; }

    /// <summary>
    /// Pre-filled field values extracted from conversation context.
    /// </summary>
    public Dictionary<string, object>? FieldSuggestions { get; set; }

    /// <summary>
    /// Structured data cards for rich rendering in the chat sidebar.
    /// </summary>
    public List<AgentCard>? Cards { get; set; }
}
