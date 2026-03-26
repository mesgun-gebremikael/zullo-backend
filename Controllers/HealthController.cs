using Microsoft.AspNetCore.Mvc;
using Zullo.Api.Dtos;

namespace Zullo.Api.Controllers;

[ApiController]
[Route("health")]
public class HealthController : ControllerBase
{
    private readonly IWebHostEnvironment _env;

    public HealthController(IWebHostEnvironment env)
    {
        _env = env;
    }

    [HttpGet]
    public IActionResult Get()
    {
        return Ok(new HealthResponseDto
        {
            Status = "ok",
            Environment = _env.EnvironmentName,
            UtcNow = DateTime.UtcNow
        });
    }
}
