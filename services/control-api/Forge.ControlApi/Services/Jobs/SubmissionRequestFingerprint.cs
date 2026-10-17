using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Forge.ControlApi.Contracts.Jobs;

namespace Forge.ControlApi.Services.Jobs;

public static class SubmissionRequestFingerprint
{
    public static string Create(SubmitJobRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var stream = new MemoryStream();

        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();

            writer.WriteString(
                "contractVersion",
                "forge-submit-v1");

            writer.WriteString(
                "jobType",
                request.JobType);

            writer.WriteBoolean(
                "hasInputPayload",
                request.InputPayload.HasValue);

            if (request.InputPayload is { } payload)
            {
                writer.WritePropertyName("inputPayload");
                WriteCanonicalJson(writer, payload);
            }

            writer.WriteString(
                "inputReference",
                request.InputReference);

            writer.WriteString(
                "inputRevision",
                request.InputRevision);

            writer.WriteEndObject();
        }

        var hash = SHA256.HashData(stream.ToArray());

        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void WriteCanonicalJson(
        Utf8JsonWriter writer,
        JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();

                foreach (var property in element
                    .EnumerateObject()
                    .OrderBy(
                        property => property.Name,
                        StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);

                    WriteCanonicalJson(
                        writer,
                        property.Value);
                }

                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();

                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonicalJson(writer, item);
                }

                writer.WriteEndArray();
                break;

            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;

            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetRawText());
                break;

            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;

            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;

            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;

            default:
                throw new ArgumentException(
                    "Unsupported JSON value kind.",
                    nameof(element));
        }
    }
}
