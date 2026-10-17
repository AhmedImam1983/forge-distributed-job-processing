using Forge.ControlApi.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Forge.ControlApi.Controllers;

[ApiController]
[Route("health/database")]
public sealed class DatabaseHealthController : ControllerBase
{
    private readonly ForgeDbContext _dbContext;
    private readonly ILogger<DatabaseHealthController> _logger;

    public DatabaseHealthController(
        ForgeDbContext dbContext,
        ILogger<DatabaseHealthController> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> Get(
        CancellationToken cancellationToken)
    {
        try
        {
            var canConnect = await _dbContext.Database
                .CanConnectAsync(cancellationToken);

            if (!canConnect)
            {
                return StatusCode(
                    StatusCodes.Status503ServiceUnavailable,
                    new { status = "unhealthy" });
            }

            return Ok(new { status = "healthy" });
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            _logger.LogWarning(
                "Database connectivity check failed.");

            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new { status = "unhealthy" });
        }
    }
}
