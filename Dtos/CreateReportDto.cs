using System.ComponentModel.DataAnnotations;

namespace Zullo.Api.Dtos;

public class CreateReportDto
{
    [Required]
    public Guid ReportedUserId { get; set; }

    [Required]
    [MaxLength(1000)]
    public string Reason { get; set; } = "";
}