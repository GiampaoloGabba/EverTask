using Microsoft.AspNetCore.Mvc;

namespace EverTask.Tests.Monitoring.TestData;

/// <summary>
/// A controller belonging to the HOST application (this test assembly). It must keep its natural
/// route (no /evertask-monitoring prefix) and stay out of the monitoring OpenAPI document.
/// </summary>
[ApiController]
[Route("[controller]")]
public class HostSampleController : ControllerBase
{
    [HttpGet("ping")]
    public IActionResult Ping() => Ok("pong");

    /// <summary>
    /// Probe for the host's MVC JSON defaults: numeric enums and nulls written. If monitoring
    /// registration leaked its JSON contract into the shared options, this shape would change.
    /// </summary>
    [HttpGet("json-contract")]
    public IActionResult JsonContract() => Ok(new HostJsonProbe(DayOfWeek.Friday, null));

    public record HostJsonProbe(DayOfWeek Day, string? Missing);
}
