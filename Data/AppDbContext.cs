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
    public DbSet<OnboardingDraft> OnboardingDrafts => Set<OnboardingDraft>();
    public DbSet<OwnedPhoto> OwnedPhotos => Set<OwnedPhoto>();
    public DbSet<AccountRemovalRequest> AccountRemovalRequests => Set<AccountRemovalRequest>();
    public DbSet<CommerceAccount> CommerceAccounts => Set<CommerceAccount>();
    public DbSet<PurchaseGrant> PurchaseGrants => Set<PurchaseGrant>();



    // SQLite test hosts emulate the production database's generated sequence.
    private void AssignLocalMessageSequences()
    {
        if (Database.IsNpgsql()) return;
        var added = ChangeTracker.Entries<Message>().Where(e => e.State == EntityState.Added && e.Entity.DeliverySequence == 0).ToList();
        if (added.Count == 0) return;
        var next = Messages.Max(m => (long?)m.DeliverySequence) ?? 0;
        foreach (var entry in added) entry.Entity.DeliverySequence = ++next;
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        AssignLocalMessageSequences();
        return base.SaveChangesAsync(cancellationToken);
    }

    public override int SaveChanges()
    {
        AssignLocalMessageSequences();
        return base.SaveChanges();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Block>().Property(b => b.SnapshotDisplayName).HasMaxLength(100);
        modelBuilder.Entity<Block>().Property(b => b.SnapshotPhotoUrl).HasMaxLength(2048);
        modelBuilder.Entity<Profile>().HasIndex(p => new { p.IsVisible, p.Lat });
        modelBuilder.Entity<Profile>().Property(p => p.PromptKey).HasMaxLength(60);
        modelBuilder.Entity<Profile>().Property(p => p.PromptAnswer).HasMaxLength(250);
        modelBuilder.Entity<Profile>().Property(p => p.DiscoveryLocationName).HasMaxLength(150);
        modelBuilder.Entity<User>().Property(u => u.DiscoveryMinAge).HasDefaultValue(18);
        modelBuilder.Entity<User>().Property(u => u.DiscoveryMaxAge).HasDefaultValue(99);
        modelBuilder.Entity<OnboardingDraft>().HasKey(d => d.UserId);
        modelBuilder.Entity<OnboardingDraft>().Property(d => d.DataJson).HasColumnType("jsonb");
        modelBuilder.Entity<OnboardingDraft>().HasOne<User>().WithMany().HasForeignKey(d => d.UserId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<OwnedPhoto>().HasKey(p => p.Id);
        modelBuilder.Entity<OwnedPhoto>().Property(p => p.Aliases).HasColumnType("jsonb");
        modelBuilder.Entity<OwnedPhoto>().Property(p => p.State).HasMaxLength(30);
        modelBuilder.Entity<OwnedPhoto>().Property(p => p.CloudName).HasMaxLength(100);
        modelBuilder.Entity<OwnedPhoto>().Property(p => p.PublicId).HasMaxLength(250);
        modelBuilder.Entity<OwnedPhoto>().Property(p => p.ProviderAssetId).HasMaxLength(100);
        modelBuilder.Entity<OwnedPhoto>().Property(p => p.FileHash).HasMaxLength(64);
        modelBuilder.Entity<OwnedPhoto>().Property(p => p.LastErrorCode).HasMaxLength(80);
        modelBuilder.Entity<OwnedPhoto>().Property(p => p.LegacyApprovalDigest).HasMaxLength(64);
        modelBuilder.Entity<OwnedPhoto>().HasIndex(p => new { p.CloudName, p.PublicId }).IsUnique();
        modelBuilder.Entity<OwnedPhoto>().HasIndex(p => new { p.UserId, p.CreatedAtUtc });
        modelBuilder.Entity<OwnedPhoto>().HasIndex(p => new { p.State, p.NextAttemptUtc });
        modelBuilder.Entity<AccountRemovalRequest>().Property(r => r.PhotoUrls).HasColumnType("jsonb");
        modelBuilder.Entity<AccountRemovalRequest>().HasIndex(r => r.FormerUserId).IsUnique();
        modelBuilder.Entity<CommerceAccount>().HasKey(a => a.UserId);
        modelBuilder.Entity<CommerceAccount>().HasOne<User>().WithOne().HasForeignKey<CommerceAccount>(a => a.UserId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<PurchaseGrant>().HasKey(g => g.TransactionKey);
        modelBuilder.Entity<PurchaseGrant>().Property(g => g.TransactionKey).HasMaxLength(220);
        modelBuilder.Entity<PurchaseGrant>().Property(g => g.ProductId).HasMaxLength(100);
        modelBuilder.Entity<PurchaseGrant>().HasOne<User>().WithMany().HasForeignKey(g => g.UserId).OnDelete(DeleteBehavior.Cascade);
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
    .Property(x => x.Email)
    .HasMaxLength(200);

        modelBuilder.Entity<User>()
            .Property(x => x.PhoneNumber)
            .HasMaxLength(50);

        modelBuilder.Entity<User>()
            .Property(x => x.PasswordHash)
            .HasMaxLength(255);

        modelBuilder.Entity<User>()
            .Property(x => x.GoogleSubject)
            .HasMaxLength(255);

        modelBuilder.Entity<User>()
     .HasOne(u => u.Profile)
     .WithOne(p => p.User)
     .HasForeignKey<Profile>(p => p.UserId)
     .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<Profile>()
    .Property(p => p.DisplayName)
    .HasMaxLength(100);

        modelBuilder.Entity<Profile>()
            .Property(p => p.Gender)
            .HasMaxLength(30);

        modelBuilder.Entity<Profile>()
            .Property(p => p.Bio)
            .HasMaxLength(1000);

        modelBuilder.Entity<Profile>()
            .Property(p => p.CountryCode)
            .HasMaxLength(10);

        modelBuilder.Entity<Profile>().Property(p => p.InterestedIn).HasMaxLength(30);

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
            e.Property(x => x.Reason)
              .HasMaxLength(1000);

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
            if (Database.IsNpgsql()) e.Property(x => x.DeliverySequence).UseIdentityByDefaultColumn();
            else e.Property(x => x.DeliverySequence).ValueGeneratedNever();
            e.HasIndex(x => x.DeliverySequence).IsUnique();
            e.HasIndex(x => new { x.FromUserId, x.ToUserId, x.DeliverySequence });
            e.HasIndex(x => new { x.FromUserId, x.ToUserId, x.ClientMessageId }).HasFilter("\"DeliveryKey\" IS NULL");
            e.Property(x => x.DeliveryKey).HasMaxLength(100);
            e.HasIndex(x => new { x.FromUserId, x.ToUserId, x.DeliveryKey })
                .IsUnique().HasFilter("\"DeliveryKey\" IS NOT NULL");

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
