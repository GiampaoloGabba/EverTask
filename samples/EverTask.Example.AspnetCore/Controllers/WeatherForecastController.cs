using EverTask.Abstractions;
using Microsoft.AspNetCore.Mvc;

namespace EverTask.Example.AspnetCore.Controllers;

[ApiController]
[Route("[controller]")]
public class WeatherForecastController : ControllerBase
{
    private static readonly string[] Summaries = new[]
    {
        "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
    };

    private readonly ILogger<WeatherForecastController> _logger;
    private readonly ITaskDispatcher _dispatcher;

    public WeatherForecastController(ILogger<WeatherForecastController> logger, ITaskDispatcher dispatcher)
    {
        _logger          = logger;
        _dispatcher = dispatcher;
    }

    [HttpGet(Name = "GetWeatherForecast")]
    public IEnumerable<WeatherForecast> Get()
    {
        _dispatcher.Dispatch(new SampleTaskRequest("Hello World"));

        return Enumerable.Range(1, 5).Select(index => new WeatherForecast
                         {
                             Date         = DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
                             TemperatureC = Random.Shared.Next(-20, 55),
                             Summary      = Summaries[Random.Shared.Next(Summaries.Length)]
                         })
                         .ToArray();
    }
}
