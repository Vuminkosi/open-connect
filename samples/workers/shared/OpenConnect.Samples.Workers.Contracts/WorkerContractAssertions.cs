using System.Text.Json;
using OpenConnect.Abstractions;
using OpenConnect.Privacy;

namespace OpenConnect.Samples.Workers.Contracts;

public sealed record AdvisoryClassificationOutcome(
    bool Valid,
    string? PredictedLabel,
    double Confidence,
    bool Abstained,
    string? ErrorCode,
    bool AttemptedPrivilegeEscalationBlocked);

public sealed record TranslationValidationOutcome(
    bool Allowed,
    string? TranslatedText,
    string? ErrorCode,
    string? ModelRevision);

public static class WorkerContractAssertions
{
    public static bool ValidateDetectorSpans(string content, IReadOnlyList<EntitySpan> spans, out string? error)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(spans);

        if (spans.Count > 1024)
        {
            error = "oversized_detector_spans";
            return false;
        }

        foreach (var span in spans)
        {
            if (span.Start < 0 || span.Length <= 0 || span.Start + span.Length > content.Length)
            {
                error = "invalid_span_bounds";
                return false;
            }
            if (string.IsNullOrWhiteSpace(span.EntityType))
            {
                error = "missing_entity_type";
                return false;
            }
            // Reject spans splitting UTF-16 surrogate pairs
            if (span.Start > 0 && char.IsLowSurrogate(content[span.Start]))
            {
                error = "utf16_surrogate_split_start";
                return false;
            }
            var endIdx = span.Start + span.Length;
            if (endIdx < content.Length && char.IsLowSurrogate(content[endIdx]))
            {
                error = "utf16_surrogate_split_end";
                return false;
            }
        }

        error = null;
        return true;
    }

    public static AdvisoryClassificationOutcome EvaluateAdvisoryClassificationResponse(
        string rawJson,
        IReadOnlySet<string> allowedLabelSet,
        IReadOnlySet<string> supportedLocales)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;

            var escalationBlocked = root.TryGetProperty("toolGrant", out _) ||
                                    root.TryGetProperty("requiredScopes", out _) ||
                                    root.TryGetProperty("elevatePrivileges", out _);

            if (root.TryGetProperty("locale", out var localeProp))
            {
                var locale = localeProp.GetString() ?? string.Empty;
                if (!supportedLocales.Contains(locale))
                {
                    return new AdvisoryClassificationOutcome(false, null, 0, true, "unsupported_locale", escalationBlocked);
                }
            }

            var abstained = root.TryGetProperty("abstain", out var abstainProp) && abstainProp.GetBoolean();
            if (abstained)
            {
                return new AdvisoryClassificationOutcome(true, "unknown", 0.0, true, null, escalationBlocked);
            }

            var label = root.TryGetProperty("label", out var labelProp) ? labelProp.GetString() : null;
            var score = root.TryGetProperty("score", out var scoreProp) && scoreProp.TryGetDouble(out var s) ? s : double.NaN;

            if (string.IsNullOrWhiteSpace(label) || !allowedLabelSet.Contains(label))
            {
                return new AdvisoryClassificationOutcome(false, null, 0, false, "unknown_label", escalationBlocked);
            }
            if (double.IsNaN(score) || score < 0.0 || score > 1.0)
            {
                return new AdvisoryClassificationOutcome(false, null, 0, false, "invalid_calibrated_score", escalationBlocked);
            }

            return new AdvisoryClassificationOutcome(true, label, score, false, null, escalationBlocked);
        }
        catch (JsonException)
        {
            return new AdvisoryClassificationOutcome(false, null, 0, false, "invalid_classifier_json", false);
        }
    }

    public static TranslationValidationOutcome ValidateTranslationRequest(
        string text,
        string sourceLang,
        string targetLang,
        IReadOnlySet<string> allowedPairs,
        int maxCharacters,
        bool commercialDeployment,
        bool checkpointCommercialAllowed)
    {
        if (commercialDeployment && !checkpointCommercialAllowed)
        {
            return new TranslationValidationOutcome(false, null, "license_denied", null);
        }
        if (string.IsNullOrWhiteSpace(text))
        {
            return new TranslationValidationOutcome(false, null, "invalid_request_empty_text", null);
        }
        if (text.Length > maxCharacters)
        {
            return new TranslationValidationOutcome(false, null, "invalid_request_text_too_long", null);
        }
        var pair = $"{sourceLang}->{targetLang}";
        if (!allowedPairs.Contains(pair))
        {
            return new TranslationValidationOutcome(false, null, "unsupported_language_pair", null);
        }

        return new TranslationValidationOutcome(true, $"[{targetLang}] {text}", null, "nllb-200-distilled-600M@rev1");
    }
}
