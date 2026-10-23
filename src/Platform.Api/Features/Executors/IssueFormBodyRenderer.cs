using System.Text;
using Platform.Api.Features.Catalog.Models;

namespace Platform.Api.Features.Executors;

/// <summary>
/// Reproduces the markdown GitHub writes when someone submits an issue form in the web UI:
/// one <c>### Label</c> heading per form element, a blank line, the value (or
/// <c>_No response_</c> for an empty optional field), a blank line. Automation that parses
/// template-created issues (e.g. issue-forms-body-parser) matches on exactly this shape, so
/// a request submitted from InfraPilot has to look indistinguishable from one typed into GitHub.
/// </summary>
public static class IssueFormBodyRenderer
{
    public const string NoResponse = "_No response_";

    public static string Render(IEnumerable<(string Label, string? Value)> sections)
    {
        var sb = new StringBuilder();
        var first = true;

        foreach (var (label, value) in sections)
        {
            if (string.IsNullOrWhiteSpace(label))
                continue;

            if (!first)
                sb.Append("\n\n");
            first = false;

            var text = value?.Trim();
            sb.Append("### ").Append(label.Trim()).Append("\n\n")
              .Append(string.IsNullOrEmpty(text) ? NoResponse : text);
        }

        return sb.ToString();
    }

    /// <summary>
    /// What an issue form would have recorded for a catalog input: the option label for a
    /// Select-style input (the form stores a dropdown's display text, the portal stores the option
    /// id), each selected label joined with ", " for a multi-select, the raw text otherwise.
    /// </summary>
    public static string DisplayValue(CatalogInput? input, string? raw)
    {
        if (string.IsNullOrEmpty(raw))
            return "";

        var options = input?.Options;
        if (options is null || options.Count == 0)
            return raw;

        if (raw.StartsWith('[') && raw.EndsWith(']'))
        {
            try
            {
                var ids = System.Text.Json.JsonSerializer.Deserialize<List<string>>(raw) ?? [];
                return string.Join(", ", ids.Select(id => LabelFor(options, id)));
            }
            catch (System.Text.Json.JsonException)
            {
                return raw;
            }
        }

        return LabelFor(options, raw);
    }

    private static string LabelFor(List<CatalogInputOption> options, string id)
        => options.FirstOrDefault(o => string.Equals(o.Id, id, StringComparison.Ordinal))?.Label ?? id;
}
