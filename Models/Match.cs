namespace Zullo.Api.Models
{
    public class Match
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid UserAId { get; set; }
        public Guid UserBId { get; set; }
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    }
}
