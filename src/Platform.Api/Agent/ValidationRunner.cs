using System.Text.RegularExpressions;
using Platform.Api.Features.Catalog;
using Platform.Api.Features.Catalog.Models;

namespace Platform.Api.Agent;

public class ValidationRunner
{
    private readonly ILogger<ValidationRunner> _logger;

    public ValidationRunner(ILogger<ValidationRunner> logger)
    {
        _logger = logger;
    }

    public async Task<ValidationResponse> Validate(CatalogDefinition definition, Dictionary<string, object?> formData)
    {
        var results = new List<FieldValidationResult>();

        // Static validations (required, pattern)
        foreach (var input in definition.Inputs)
        {
            formData.TryGetValue(input.Id, out var value);
            var valueStr = value?.ToString();

            // Required check
            if (input.Required && string.IsNullOrWhiteSpace(valueStr))
            {
                results.Add(new FieldValidationResult(input.Id, false, $"{input.Label} is required"));
                continue;
            }

            // Pattern check
            if (!string.IsNullOrWhiteSpace(input.Validation) && !string.IsNullOrWhiteSpace(valueStr))
            {
                if (!Regex.IsMatch(valueStr, input.Validation))
                {
                    results.Add(new FieldValidationResult(input.Id, false,
                        $"{input.Label} does not match the required format: {input.Validation}"));
                    continue;
                }
            }

            if (!string.IsNullOrWhiteSpace(valueStr))
            {
                results.Add(new FieldValidationResult(input.Id, true, null));
            }
        }

        // Dynamic validations (api-check, lookup, dns-check) — stubs for now
        foreach (var validation in definition.Validations)
        {
            _logger.LogInformation("Running dynamic validation: {Id} ({Type})", validation.Id, validation.Type);
            // TODO: Implement keyed validation checkers
            results.Add(new FieldValidationResult(validation.TargetField ?? validation.Id, true, null));
        }

        return new ValidationResponse(results.All(r => r.Passed), results);
    }
}

public record ValidationResponse(bool IsValid, List<FieldValidationResult> Results);
public record FieldValidationResult(string FieldId, bool Passed, string? Message);
