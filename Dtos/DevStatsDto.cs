namespace Zullo.Api.Dtos;

public class DevStatsDto
{
    // Totalt antal profiler i databasen
    public int ProfilesTotal { get; set; }

    // Hur många profiler som är synliga i feeden
    public int ProfilesVisible { get; set; }

    public int Likes { get; set; }
    public int Skips { get; set; }
}
