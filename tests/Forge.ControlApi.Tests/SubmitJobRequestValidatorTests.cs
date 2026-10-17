using System.Text.Json;
using Forge.ControlApi.Contracts.Jobs;
using Forge.ControlApi.Services.Jobs;

namespace Forge.ControlApi.Tests;

public class SubmitJobRequestValidatorTests
{
    [Fact]
    public void Validate_ValidInlinePayload_ReturnsNoError()
    {
        var request = CreateRequest(
            payloadJson: """{"text":"database connection failed"}""");

        var error = SubmitJobRequestValidator.Validate(request);

        Assert.Null(error);
    }

    [Fact]
    public void Validate_EmptySubmissionRequestId_ReturnsError()
    {
        var request = CreateRequest(
            submissionRequestId: Guid.Empty);

        var error = SubmitJobRequestValidator.Validate(request);

        Assert.Equal("InvalidSubmissionRequestId", error?.Code);
    }

    [Fact]
    public void Validate_UnsupportedJobType_ReturnsError()
    {
        var request = CreateRequest(jobType: "unknown-job");

        var error = SubmitJobRequestValidator.Validate(request);

        Assert.Equal("UnsupportedJobType", error?.Code);
    }

    [Fact]
    public void Validate_MissingInput_ReturnsError()
    {
        var request = CreateRequest(payloadJson: null);

        var error = SubmitJobRequestValidator.Validate(request);

        Assert.Equal("MissingInput", error?.Code);
    }

    [Fact]
    public void Validate_EmptyText_ReturnsError()
    {
        var request = CreateRequest(
            payloadJson: """{"text":"   "}""");

        var error = SubmitJobRequestValidator.Validate(request);

        Assert.Equal("InvalidInputSchema", error?.Code);
    }

    [Fact]
    public void Validate_AdditionalPayloadProperty_ReturnsError()
    {
        var request = CreateRequest(
            payloadJson: """{"text":"some logs","extra":true}""");

        var error = SubmitJobRequestValidator.Validate(request);

        Assert.Equal("InvalidInputSchema", error?.Code);
    }

    [Fact]
    public void Validate_PayloadAndArtifactReference_ReturnsError()
    {
        var request = CreateRequest(
            payloadJson: """{"text":"some logs"}""",
            inputReference: "artifact://logs/server-01",
            inputRevision: "version:v1");

        var error = SubmitJobRequestValidator.Validate(request);

        Assert.Equal(
            "ConflictingInputRepresentations",
            error?.Code);
    }

    [Fact]
    public void Validate_ValidArtifactReference_ReturnsNoError()
    {
        var request = CreateRequest(
            payloadJson: null,
            inputReference: "artifact://logs/server-01",
            inputRevision: "version:v1");

        var error = SubmitJobRequestValidator.Validate(request);

        Assert.Null(error);
    }

    [Fact]
    public void Validate_InvalidArtifactRevision_ReturnsError()
    {
        var request = CreateRequest(
            payloadJson: null,
            inputReference: "artifact://logs/server-01",
            inputRevision: "latest");

        var error = SubmitJobRequestValidator.Validate(request);

        Assert.Equal("InvalidInputRevision", error?.Code);
    }

    [Fact]
    public void Validate_HttpsReference_ReturnsError()
    {
        var request = CreateRequest(
            payloadJson: null,
            inputReference: "https://example.com/logs.txt",
            inputRevision: "version:v1");

        var error = SubmitJobRequestValidator.Validate(request);

        Assert.Equal("InvalidArtifactReference", error?.Code);
    }

    [Fact]
    public void Validate_OversizedPayload_ReturnsError()
    {
        var largeText = new string(
            'a',
            SubmitJobRequestValidator.MaxInlinePayloadBytes);

        var json = JsonSerializer.Serialize(
            new { text = largeText });

        var request = CreateRequest(payloadJson: json);

        var error = SubmitJobRequestValidator.Validate(request);

        Assert.Equal("InputPayloadTooLarge", error?.Code);
    }

    private static SubmitJobRequest CreateRequest(
        Guid? submissionRequestId = null,
        string jobType = "log-analysis",
        string? payloadJson = """{"text":"hello"}""",
        string? inputReference = null,
        string? inputRevision = null)
    {
        JsonElement? payload = null;

        if (payloadJson is not null)
        {
            using var document = JsonDocument.Parse(payloadJson);
            payload = document.RootElement.Clone();
        }

        return new SubmitJobRequest
        {
            SubmissionRequestId =
                submissionRequestId ?? Guid.NewGuid(),
            JobType = jobType,
            InputPayload = payload,
            InputReference = inputReference,
            InputRevision = inputRevision
        };
    }
}
