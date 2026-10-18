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
    private readonly ClaimJobService _claimJobService;
    private readonly LeaseRenewalService _leaseRenewalService;

    public JobsController(
        SubmitJobService submitJobService,
        ClaimJobService claimJobService,
        LeaseRenewalService leaseRenewalService)
    {
        _submitJobService = submitJobService;
        _claimJobService = claimJobService;
        _leaseRenewalService = leaseRenewalService;
    }



    [HttpPost("renew-lease")]
    [ProducesResponseType(
        typeof(RenewLeaseResponse),
        StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RenewLease(
        [FromBody] RenewLeaseRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _leaseRenewalService.RenewAsync(
            request,
            cancellationToken);

        return new ContentResult
        {
            StatusCode = result.StatusCode,
            ContentType = "application/json; charset=utf-8",
            Content = result.ResponseBodyJson
        };
    }

    [HttpPost("claim")]
    [ProducesResponseType(
        typeof(ClaimJobResponse),
        StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Claim(
        [FromBody] ClaimJobRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _claimJobService.ClaimAsync(
            request,
            cancellationToken);

        return new ContentResult
        {
            StatusCode = result.StatusCode,
            ContentType = "application/json; charset=utf-8",
            Content = result.ResponseBodyJson
        };
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
