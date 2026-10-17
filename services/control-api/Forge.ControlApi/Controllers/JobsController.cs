using Forge.ControlApi.Contracts.Jobs;
using Forge.ControlApi.Services.Jobs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Forge.ControlApi.Controllers;

[ApiController]
[Route("api/v1/jobs")]
[Authorize]
public sealed class JobsController : ControllerBase
{
    private readonly SubmitJobService _submitJobService;

    public JobsController(SubmitJobService submitJobService)
    {
        _submitJobService = submitJobService;
    }

    [HttpPost]
    [ProducesResponseType(
        typeof(JobSubmissionReceipt),
        StatusCodes.Status202Accepted)]
    public async Task<IActionResult> Submit(
        [FromBody] SubmitJobRequest request,
        CancellationToken cancellationToken)
    {
        // With MapInboundClaims=false, the Keycloak claim
        // remains available under its original name: "azp".
        var clientScopeId = User.FindFirst("azp")?.Value;

        if (string.IsNullOrWhiteSpace(clientScopeId))
        {
            return Forbid();
        }

        var result = await _submitJobService.SubmitAsync(
            clientScopeId,
            request,
            cancellationToken);

        return new ContentResult
        {
            StatusCode = result.StatusCode,
            ContentType = "application/json; charset=utf-8",
            Content = result.ResponseBodyJson
        };
    }
}
