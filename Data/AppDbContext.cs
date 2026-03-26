using Microsoft.EntityFrameworkCore;
using Zullo.Api.Models;

namespace Zullo.Api.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>(); public DbSet<Profile> Profiles => Set<Profile>();

    public DbSet<Like> Likes => Set<Like>();
    public DbSet<Match> Matches => Set<Match>();
    public DbSet<Skip> Skips => Set<Skip>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<Report> Reports => Set<Report>();
    public DbSet<Block> Blocks => Set<Block>();



    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        //kopplar Dbset Users -> tabell "user"
        modelBuilder.Entity<User>().ToTable("User");

        modelBuilder.Entity<User>()
        .HasIndex(x => x.Email)
        .IsUnique();

        modelBuilder.Entity<User>()
            .HasIndex(x => x.PhoneNumber);

        modelBuilder.Entity<User>()
            .HasIndex(x => x.GoogleSubject)
            .IsUnique();

        modelBuilder.Entity<User>()
     .HasOne(u => u.Profile)
     .WithOne(p => p.User)
     .HasForeignKey<Profile>(p => p.UserId)
     .OnDelete(DeleteBehavior.Cascade);

        // Store lists as JSON in Postgres (simple for v1)
        modelBuilder.Entity<Profile>()
            .Property(p => p.Interests)
            .HasColumnType("jsonb");

        modelBuilder.Entity<Profile>()
            .Property(p => p.PhotoUrls)
            .HasColumnType("jsonb");

        modelBuilder.Entity<Like>(e =>
        {
            // Hindrar samma like från att sparas flera gånger
            e.HasIndex(x => new { x.FromUserId, x.ToUserId }).IsUnique();

            e.HasOne<User>()
          .WithMany()
          .HasForeignKey(x => x.FromUserId)
          .OnDelete(DeleteBehavior.Restrict);

            e.HasOne<User>()
                .WithMany()
                .HasForeignKey(x => x.ToUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });



        modelBuilder.Entity<Skip>(e =>
        {
            // Hindrar samma skip från att sparas flera gånger
            e.HasIndex(x => new { x.FromUserId, x.ToUserId }).IsUnique();

            e.HasOne<User>()
                .WithMany()
                .HasForeignKey(x => x.FromUserId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasOne<User>()
                .WithMany()
                .HasForeignKey(x => x.ToUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<Block>(e =>
        {
            // Hindrar samma block från att sparas flera gånger
            e.HasIndex(x => new { x.FromUserId, x.BlockedUserId }).IsUnique();

            e.HasOne<User>()
                .WithMany()
                .HasForeignKey(x => x.FromUserId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasOne<User>()
                .WithMany()
                .HasForeignKey(x => x.BlockedUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Report>(e =>
        {
            e.HasOne<User>()
                .WithMany()
                .HasForeignKey(x => x.FromUserId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasOne<User>()
                .WithMany()
                .HasForeignKey(x => x.ReportedUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Match>(e =>
        {
            // En match mellan två användare ska bara kunna finnas en gång
            e.HasIndex(x => new { x.UserAId, x.UserBId }).IsUnique();

            e.HasOne<User>()
                .WithMany()
                .HasForeignKey(x => x.UserAId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasOne<User>()
                .WithMany()
                .HasForeignKey(x => x.UserBId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Message>(e =>
        {
            e.HasKey(x => x.Id);

            e.Property(x => x.Text)
                .IsRequired()
                .HasMaxLength(2000);

            e.HasIndex(x => new { x.FromUserId, x.ToUserId, x.CreatedAtUtc });
            e.HasIndex(x => new { x.ToUserId, x.CreatedAtUtc });

            e.HasOne<User>()
          .WithMany()
           .HasForeignKey(x => x.FromUserId)
           .OnDelete(DeleteBehavior.Restrict);

            e.HasOne<User>()
                .WithMany()
                .HasForeignKey(x => x.ToUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

    }
}
