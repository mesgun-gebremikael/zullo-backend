using Microsoft.EntityFrameworkCore;
using Zullo.Api.Models;

namespace Zullo.Api.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<User> User => Set<User>();
    public DbSet<Profile> Profiles => Set<Profile>();

    public DbSet<Like> Likes => Set<Like>();
    public DbSet<Match> Matches => Set<Match>();
    public DbSet<Skip> Skips => Set<Skip>();
    public DbSet<Message> Messages => Set<Message>();



    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>()
            .HasIndex(x => x.Email);

        modelBuilder.Entity<User>()
            .HasIndex(x => x.PhoneNumber);

        modelBuilder.Entity<User>()
            .HasIndex(x => x.GoogleSubject);

        modelBuilder.Entity<User>()
            .HasOne(u => u.Profile)
            .WithOne(p => p.User)
            .HasForeignKey<Profile>(p => p.UserId);

        // Store lists as JSON in Postgres (simple for v1)
        modelBuilder.Entity<Profile>()
            .Property(p => p.Interests)
            .HasColumnType("jsonb");

        modelBuilder.Entity<Profile>()
            .Property(p => p.PhotoUrls)
            .HasColumnType("jsonb");

        modelBuilder.Entity<Message>(e =>
        {
            e.HasKey(x => x.Id);

            e.Property(x => x.Text)
                .IsRequired()
                .HasMaxLength(2000);

            e.HasIndex(x => new { x.FromUserId, x.ToUserId, x.CreatedAtUtc });
            e.HasIndex(x => new { x.ToUserId, x.CreatedAtUtc });
        });

    }
}
