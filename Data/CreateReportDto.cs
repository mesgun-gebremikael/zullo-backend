namespace Zullo.Api.Dtos;

public class CreateReportDto
{
    public Guid ReportedUserId { get; set; }
    public string Reason { get; set; } = "";
}