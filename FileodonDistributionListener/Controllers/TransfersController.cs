using FileodonDistributionListener.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FileodonDistributionListener.Controllers;

[ApiController]
[Route("api/transfers")]
[Authorize]
public sealed class TransfersController(TransferQueue queue, TransferJobStore store) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType(typeof(TransferJobSnapshot), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Create([FromBody] TransferRequest request, CancellationToken cancellationToken)
    {
        if (request.Files.Count == 0)
        {
            return BadRequest("At least one file is required.");
        }
        if (request.Files.Any(file =>
                string.IsNullOrWhiteSpace(file.SourceRemotePath) ||
                string.IsNullOrWhiteSpace(file.DestinationRemotePath) ||
                file.Length < 0))
        {
            return BadRequest("Each file must have valid source and destination paths and a non-negative length.");
        }

        TransferJobSnapshot snapshot = store.Create(request);
        await queue.EnqueueAsync(new TransferWorkItem(snapshot.JobId, request), cancellationToken);
        return AcceptedAtAction(nameof(Poll), new { jobId = snapshot.JobId }, snapshot);
    }

    [HttpGet("{jobId:guid}")]
    [ProducesResponseType(typeof(TransferJobSnapshot), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public IActionResult Poll(Guid jobId)
    {
        TransferJobSnapshot? snapshot = store.Get(jobId);
        return snapshot is null ? NotFound("Transfer job not found.") : Ok(snapshot);
    }
}
