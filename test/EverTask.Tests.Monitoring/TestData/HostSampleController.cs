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
}
