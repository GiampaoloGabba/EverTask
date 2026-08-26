using EverTask.Monitor.Api.DTOs.Management;
using EverTask.Monitor.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace EverTask.Monitor.Api.Controllers;

/// <summary>
/// The write surface of the monitoring API: requeue an occurrence, resume a halted catch-up, cancel a
/// schedule.
/// </summary>
/// <remarks>
/// <para>
/// Every route here is refused unless <c>EnableManagementEndpoints</c> is on (404 otherwise) AND the caller
/// carries the operate role or passes the host's <c>ManagementAuthorization</c> hook (403 otherwise). The
/// check is in <c>JwtAuthenticationMiddleware</c>, which gates the whole <c>api/management</c> prefix, so a
/// route added here cannot forget to ask for it.
/// </para>
/// <para>
/// <b>CSRF does not apply.</b> The API authenticates a session with a Bearer token in the
/// <c>Authorization</c> header, never with a cookie, and the <c>?access_token=</c> fallback exists on the
/// SignalR hub path alone. A browser attaches neither to a cross-site request, so a page the operator did
/// not open cannot make one of these calls in their name — which is why these endpoints need no anti-forgery
/// token of their own, and why the dashboard must keep its token out of cookies.
/// </para>
/// </remarks>
[ApiController]
[Route("api/management")]
public class ManagementController(IManagementService managementService) : ControllerBase
{
    /// <summary>
    /// Put a terminal occurrence back in the queue, keeping its id, its history and its audit trail.
    /// </summary>
    /// <param name="id">The occurrence row. A schedule row is refused.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The outcome of the operation.</returns>
    /// <response code="200">The occurrence was requeued.</response>
    /// <response code="401">Unauthorized - JWT token required.</response>
    /// <response code="403">The session does not carry the operate role.</response>
    /// <response code="404">Management endpoints are disabled, or no task carries that id.</response>
    /// <response code="409">The row is not an occurrence, or is not terminal, so there is nothing to requeue.</response>
    /// <response code="501">No EverTask host is registered, or the storage does not implement durable occurrences.</response>
    [HttpPost("tasks/{id:guid}/requeue")]
    [ProducesResponseType(typeof(ManagementActionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ManagementActionDto), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ManagementActionDto), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ManagementActionDto>> Requeue(Guid id, CancellationToken ct) =>
        Respond(await managementService.RequeueOccurrenceAsync(id, ct).ConfigureAwait(false));

    /// <summary>
    /// Release a durable catch-up that halted itself, keeping the schedule's cursor and therefore its backlog.
    /// </summary>
    /// <param name="id">The schedule row.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The outcome of the operation, with the slot the schedule will fire next.</returns>
    /// <response code="200">The schedule was handed back to the scheduler.</response>
    /// <response code="401">Unauthorized - JWT token required.</response>
    /// <response code="403">The session does not carry the operate role.</response>
    /// <response code="404">Management endpoints are disabled, or no task carries that id.</response>
    /// <response code="409">The row is not a schedule, carries no task key, or was cancelled.</response>
    /// <response code="501">No EverTask host is registered, or the storage does not implement schedule versioning.</response>
    /// <response code="503">The schedule's occurrence provider could not answer; nothing was written.</response>
    [HttpPost("tasks/{id:guid}/resume")]
    [ProducesResponseType(typeof(ManagementActionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ManagementActionDto), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ManagementActionDto), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ManagementActionDto>> Resume(Guid id, CancellationToken ct) =>
        Respond(await managementService.ResumeScheduleAsync(id, ct).ConfigureAwait(false));

    /// <summary>
    /// Cancel a schedule and every occurrence of it still pending. Terminal: it has to be dispatched again.
    /// </summary>
    /// <param name="id">The schedule row.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The outcome of the operation.</returns>
    /// <response code="200">The schedule was cancelled.</response>
    /// <response code="401">Unauthorized - JWT token required.</response>
    /// <response code="403">The session does not carry the operate role.</response>
    /// <response code="404">Management endpoints are disabled, or no task carries that id.</response>
    /// <response code="409">The row is not a schedule, or carries no task key.</response>
    /// <response code="501">No EverTask host is registered.</response>
    [HttpPost("tasks/{id:guid}/cancel")]
    [ProducesResponseType(typeof(ManagementActionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ManagementActionDto), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ManagementActionDto), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ManagementActionDto>> Cancel(Guid id, CancellationToken ct) =>
        Respond(await managementService.CancelScheduleAsync(id, ct).ConfigureAwait(false));

    /// <summary>
    /// The one place an outcome becomes a status code, so the three endpoints cannot map it differently.
    /// The body is the same DTO in every case: a refusal that carries no reason is a refusal an operator
    /// cannot act on.
    /// </summary>
    private ActionResult<ManagementActionDto> Respond(ManagementActionDto result) =>
        result.Status switch
        {
            ManagementActionStatus.Succeeded    => Ok(result),
            ManagementActionStatus.NotFound     => NotFound(result),
            ManagementActionStatus.Conflict     => Conflict(result),
            ManagementActionStatus.NotSupported => StatusCode(StatusCodes.Status501NotImplemented, result),
            _                                   => StatusCode(StatusCodes.Status503ServiceUnavailable, result)
        };
}
