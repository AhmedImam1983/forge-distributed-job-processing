using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Forge.ControlApi.Contracts.Jobs;

namespace Forge.ControlApi.Services.Jobs;

public sealed record SubmitJobValidationError(
    string Code,
    string Message);

public static class SubmitJobRequestValidator
{
    public const int MaxInlinePayloadBytes = 64 * 1024;

    private static readonly Regex ValidRevision = new(
        @"\A(?:version:[A-Za-z0-9._~-]{1,200}|sha256:[0-9a-fA-F]{64})\z",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static SubmitJobValidationError? Validate(
        SubmitJobRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.SubmissionRequestId == Guid.Empty)
        {
            return Error(
                "InvalidSubmissionRequestId",
                "A non-empty submissionRequestId is required.");
        }

        if (string.IsNullOrWhiteSpace(request.JobType))
        {
            return Error(
                "InvalidJobType",
                "jobType is required.");
        }

        if (!string.Equals(
                request.JobType,
                "log-analysis",
                StringComparison.Ordinal))
        {
            return Error(
                "UnsupportedJobType",
                "The requested jobType is not supported.");
        }

        var hasPayload = request.InputPayload.HasValue;
        var hasReference = request.InputReference is not null;
        var hasRevision = request.InputRevision is not null;
        var hasReferenceFields = hasReference || hasRevision;

        if (hasPayload && hasReferenceFields)
        {
            return Error(
                "ConflictingInputRepresentations",
                "Provide inline input or an artifact reference, not both.");
        }

        if (!hasPayload && !hasReferenceFields)
        {
            return Error(
                "MissingInput",
                "An input payload or artifact reference is required.");
        }

        if (hasPayload)
        {
            return ValidateInlinePayload(
                request.InputPayload!.Value);
        }

        return ValidateArtifactReference(
            request.InputReference,
            request.InputRevision);
    }

    private static SubmitJobValidationError? ValidateInlinePayload(
        JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return Error(
                "InvalidInputPayload",
                "inputPayload must be a JSON object.");
        }

        if (Encoding.UTF8.GetByteCount(payload.GetRawText())
            > MaxInlinePayloadBytes)
        {
            return Error(
                "InputPayloadTooLarge",
                "The inline input payload exceeds 64 KiB.");
        }

        var properties = payload.EnumerateObject().ToArray();

        if (properties.Length != 1 ||
            !string.Equals(
                properties[0].Name,
                "text",
                StringComparison.Ordinal))
        {
            return Error(
                "InvalidInputSchema",
                "The log-analysis payload must contain only a text property.");
        }

        var textProperty = properties[0].Value;

        if (textProperty.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(textProperty.GetString()))
        {
            return Error(
                "InvalidInputSchema",
                "The text property must contain a non-empty string.");
        }

        return null;
    }

    private static SubmitJobValidationError? ValidateArtifactReference(
        string? inputReference,
        string? inputRevision)
    {
        if (string.IsNullOrWhiteSpace(inputReference) ||
            string.IsNullOrWhiteSpace(inputRevision))
        {
            return Error(
                "InvalidArtifactReference",
                "An artifact reference and immutable revision are required.");
        }

        if (inputReference.Length > 2048 ||
            !Uri.TryCreate(
                inputReference,
                UriKind.Absolute,
                out var uri) ||
            !string.Equals(
                uri.Scheme,
                "artifact",
                StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            uri.AbsolutePath == "/" ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            return Error(
                "InvalidArtifactReference",
                "The artifact reference must use a valid artifact:// URI.");
        }

        if (!ValidRevision.IsMatch(inputRevision))
        {
            return Error(
                "InvalidInputRevision",
                "The revision must use version:<id> or sha256:<64 hexadecimal characters>.");
        }

        return null;
    }

    private static SubmitJobValidationError Error(
        string code,
        string message)
    {
        return new SubmitJobValidationError(code, message);
    }
}
