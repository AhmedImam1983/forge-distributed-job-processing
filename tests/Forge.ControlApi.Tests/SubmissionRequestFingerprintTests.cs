using System.Text.Json;
using Forge.ControlApi.Contracts.Jobs;
using Forge.ControlApi.Services.Jobs;

namespace Forge.ControlApi.Tests;

public class SubmissionRequestFingerprintTests
{
    [Fact]
    public void Create_SameJsonWithDifferentPropertyOrder_ReturnsSameFingerprint()
    {
        var first = CreateRequest(
            payloadJson: """{"z":1,"nested":{"b":true,"a":"x"},"a":2}""");

        var second = CreateRequest(
            payloadJson: """{"a":2,"nested":{"a":"x","b":true},"z":1}""");

        Assert.Equal(
            SubmissionRequestFingerprint.Create(first),
            SubmissionRequestFingerprint.Create(second));
    }

    [Fact]
    public void Create_DifferentJobType_ReturnsDifferentFingerprint()
    {
        var first = CreateRequest(jobType: "log-analysis");
        var second = CreateRequest(jobType: "file-conversion");

        Assert.NotEqual(
            SubmissionRequestFingerprint.Create(first),
            SubmissionRequestFingerprint.Create(second));
    }

    [Fact]
    public void Create_DifferentPayload_ReturnsDifferentFingerprint()
    {
        var first = CreateRequest(payloadJson: """{"text":"hello"}""");
        var second = CreateRequest(payloadJson: """{"text":"goodbye"}""");

        Assert.NotEqual(
            SubmissionRequestFingerprint.Create(first),
            SubmissionRequestFingerprint.Create(second));
    }

    [Fact]
    public void Create_DifferentSubmissionRequestId_ReturnsSameFingerprint()
    {
        var first = CreateRequest();
        var second = first with
        {
            SubmissionRequestId = Guid.NewGuid()
        };

        Assert.Equal(
            SubmissionRequestFingerprint.Create(first),
            SubmissionRequestFingerprint.Create(second));
    }

    [Fact]
    public void Create_DifferentInputRevision_ReturnsDifferentFingerprint()
    {
        var first = CreateRequest(
            payloadJson: null,
            inputReference: "artifact://logs/server-01",
            inputRevision: "sha256:version-a");

        var second = CreateRequest(
            payloadJson: null,
            inputReference: "artifact://logs/server-01",
            inputRevision: "sha256:version-b");

        Assert.NotEqual(
            SubmissionRequestFingerprint.Create(first),
            SubmissionRequestFingerprint.Create(second));
    }

    [Fact]
    public void Create_DifferentArrayOrder_ReturnsDifferentFingerprint()
    {
        var first = CreateRequest(payloadJson: """{"items":[1,2,3]}""");
        var second = CreateRequest(payloadJson: """{"items":[3,2,1]}""");

        Assert.NotEqual(
            SubmissionRequestFingerprint.Create(first),
            SubmissionRequestFingerprint.Create(second));
    }

    [Fact]
    public void Create_Returns64CharacterLowercaseHexFingerprint()
    {
        var fingerprint =
            SubmissionRequestFingerprint.Create(CreateRequest());

        Assert.Equal(64, fingerprint.Length);
        Assert.Matches("^[0-9a-f]{64}$", fingerprint);
    }

    private static SubmitJobRequest CreateRequest(
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
            SubmissionRequestId = Guid.NewGuid(),
            JobType = jobType,
            InputPayload = payload,
            InputReference = inputReference,
            InputRevision = inputRevision
        };
    }
}
