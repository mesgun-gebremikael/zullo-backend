namespace Zullo.Api.Models;

public class Report
{
    public Guid Id { get; set; }

    // vem rapporterar
    public Guid FromUserId { get; set; }

    // vem blir rapporterad
    public Guid ReportedUserId { get; set; }

    public string Reason { get; set; } = "";

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
