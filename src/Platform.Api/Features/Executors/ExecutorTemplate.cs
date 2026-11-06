using System.Text.RegularExpressions;
using Platform.Api.Features.Requests.Models;

namespace Platform.Api.Features.Executors;

/// <summary>
/// Resolves <c>{{...}}</c> placeholders in an executor's <c>parameters_map</c>.
///
/// <para><c>{{field}}</c> is the value of the form input with that id. <c>{{request.id}}</c>,
/// <c>{{request.requester_email}}</c> and <c>{{request.requester_name}}</c> come from the request
/// itself, so a pipeline can tell who asked for it (for example to send the result privately).
/// A placeholder that matches nothing is left as written.</para>
///
/// <para>Replacement is a single pass over the template, so an input value that itself contains
/// <c>{{request.requester_email}}</c> is passed through literally, never expanded.</para>
/// </summary>
public static partial class ExecutorTemplate
{
    public const string RequestPrefix = "request.";

    /// <summary>The request variables a template may use, without the <c>request.</c> prefix.</summary>
    public static readonly IReadOnlyList<string> RequestVariables = ["id", "requester_email", "requester_name"];

    [GeneratedRegex(@"\{\{([\w.]+)\}\}")]
    private static partial Regex Placeholder();

    public static Dictionary<string, string> ResolveParameters(
        IReadOnlyDictionary<string, string> parametersMap,
        IReadOnlyDictionary<string, string> inputs,
        ServiceRequest request)
    {
        var resolved = new Dictionary<string, string>();
        foreach (var (key, template) in parametersMap)
            resolved[key] = Resolve(template, inputs, request);
        return resolved;
    }

    public static string Resolve(string template, IReadOnlyDictionary<string, string> inputs, ServiceRequest request) =>
        Placeholder().Replace(template, match =>
        {
            var name = match.Groups[1].Value;
            if (name.StartsWith(RequestPrefix, StringComparison.Ordinal))
                return RequestValue(name[RequestPrefix.Length..], request) ?? match.Value;
            return inputs.TryGetValue(name, out var value) ? value : match.Value;
        });

    /// <summary>
    /// The <c>{{request.*}}</c> placeholders in <paramref name="templates"/> that aren't request
    /// variables (a typo such as <c>{{request.email}}</c>), for catalog validation.
    /// </summary>
    public static IEnumerable<string> UnknownRequestVariables(IEnumerable<string> templates) =>
        templates
            .SelectMany(t => Placeholder().Matches(t ?? "").Select(m => m.Groups[1].Value))
            .Where(n => n.StartsWith(RequestPrefix, StringComparison.Ordinal)
                && !RequestVariables.Contains(n[RequestPrefix.Length..]))
            .Distinct();

    private static string? RequestValue(string variable, ServiceRequest request) => variable switch
    {
        "id" => request.Id.ToString(),
        "requester_email" => request.RequesterEmail,
        "requester_name" => request.RequesterName,
        _ => null,
    };
}
