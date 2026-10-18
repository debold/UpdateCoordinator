using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace UpdateCoordinator.Server.Data;

internal sealed class ServerDbContext(DbContextOptions<ServerDbContext> options) : IdentityDbContext<IdentityUser>(options)
{
    public DbSet<ClientEntity> Clients => Set<ClientEntity>();

    public DbSet<TrustBindingEntity> TrustBindings => Set<TrustBindingEntity>();

    public DbSet<UpdateEntity> Updates => Set<UpdateEntity>();

    public DbSet<ClientUpdateEntity> ClientUpdates => Set<ClientUpdateEntity>();

    public DbSet<ClientAvailableUpdateEntity> ClientAvailableUpdates => Set<ClientAvailableUpdateEntity>();

    public DbSet<ReleaseEntity> Releases => Set<ReleaseEntity>();

    public DbSet<InstallationEntity> Installations => Set<InstallationEntity>();

    public DbSet<AuditEntryEntity> AuditEntries => Set<AuditEntryEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Identity tables (accounts, roles) of the web administration.
        base.OnModelCreating(modelBuilder);

        var audit = modelBuilder.Entity<AuditEntryEntity>();
        audit.ToTable("AuditEntries");
        audit.HasKey(a => a.Id);
        audit.Property(a => a.Actor).HasMaxLength(256).IsRequired();
        audit.Property(a => a.Action).HasMaxLength(64).IsRequired();
        audit.Property(a => a.Target).HasMaxLength(256);
        audit.Property(a => a.Details).HasMaxLength(2048);
        audit.Property(a => a.PreviousHash).HasMaxLength(32).IsRequired();
        audit.Property(a => a.Hash).HasMaxLength(32).IsRequired();
        audit.HasIndex(a => a.AtUtc);

        var client = modelBuilder.Entity<ClientEntity>();
        client.ToTable("Clients");
        client.HasKey(c => c.Id);
        client.Property(c => c.Id).ValueGeneratedNever();
        client.Property(c => c.SecretHash).HasMaxLength(32).IsRequired();
        client.Property(c => c.State).HasConversion<int>();
        client.Property(c => c.HostName).HasMaxLength(63).IsRequired();
        client.Property(c => c.DomainName).HasMaxLength(253);
        client.Property(c => c.OsVersion).HasMaxLength(23).IsRequired();
        client.Property(c => c.AgentVersion).HasMaxLength(128).IsRequired();
        client.Property(c => c.RegisteredFromAddress).HasMaxLength(45);
        client.Property(c => c.OsProductName).HasMaxLength(128);
        client.Property(c => c.OsEditionId).HasMaxLength(64);
        client.Property(c => c.OsDisplayVersion).HasMaxLength(16);
        client.Property(c => c.WsusServer).HasMaxLength(255);
        client.OwnsMany(c => c.HealthChecks, health => health.ToJson());
        client.HasIndex(c => c.HostName);

        var binding = modelBuilder.Entity<TrustBindingEntity>();
        binding.ToTable("TrustBindings");
        binding.HasKey(b => b.Serial);
        binding.Property(b => b.Serial).ValueGeneratedNever();
        binding.Property(b => b.CertificateSpkiSha256).HasMaxLength(32).IsRequired();
        binding.Property(b => b.Signature).HasMaxLength(256).IsRequired();

        var update = modelBuilder.Entity<UpdateEntity>();
        update.ToTable("Updates");
        update.HasKey(u => new { u.UpdateId, u.Revision });
        update.Property(u => u.KbArticle).HasMaxLength(10);
        update.Property(u => u.Title).HasMaxLength(512).IsRequired();
        update.Property(u => u.Classification).HasMaxLength(64);
        update.Property(u => u.MsrcSeverity).HasMaxLength(32);
        update.HasIndex(u => u.KbArticle);

        var clientUpdate = modelBuilder.Entity<ClientUpdateEntity>();
        clientUpdate.ToTable("ClientUpdates");
        clientUpdate.HasKey(cu => new { cu.ClientId, cu.UpdateId, cu.Revision });
        clientUpdate.HasOne<ClientEntity>().WithMany().HasForeignKey(cu => cu.ClientId).OnDelete(DeleteBehavior.Cascade);
        clientUpdate.HasOne<UpdateEntity>().WithMany().HasForeignKey(cu => new { cu.UpdateId, cu.Revision }).OnDelete(DeleteBehavior.Restrict);

        var available = modelBuilder.Entity<ClientAvailableUpdateEntity>();
        available.ToTable("ClientAvailableUpdates");
        available.HasKey(a => new { a.ClientId, a.UpdateId, a.Revision });
        available.HasOne<ClientEntity>().WithMany().HasForeignKey(a => a.ClientId).OnDelete(DeleteBehavior.Cascade);
        available.HasOne<UpdateEntity>().WithMany().HasForeignKey(a => new { a.UpdateId, a.Revision }).OnDelete(DeleteBehavior.Restrict);

        var release = modelBuilder.Entity<ReleaseEntity>();
        release.ToTable("Releases");
        release.HasKey(r => new { r.ClientId, r.UpdateId, r.Revision });
        release.HasOne<ClientEntity>().WithMany().HasForeignKey(r => r.ClientId).OnDelete(DeleteBehavior.Cascade);
        release.HasOne<UpdateEntity>().WithMany().HasForeignKey(r => new { r.UpdateId, r.Revision }).OnDelete(DeleteBehavior.Restrict);

        var installation = modelBuilder.Entity<InstallationEntity>();
        installation.ToTable("Installations");
        installation.HasKey(i => i.Id);
        installation.Property(i => i.Source).HasMaxLength(32).IsRequired();
        installation.HasIndex(i => new { i.ClientId, i.RunId, i.UpdateId, i.Revision }).IsUnique();
        installation.HasIndex(i => new { i.UpdateId, i.Revision });
        installation.HasOne<ClientEntity>().WithMany().HasForeignKey(i => i.ClientId).OnDelete(DeleteBehavior.Cascade);
        installation.HasOne<UpdateEntity>().WithMany().HasForeignKey(i => new { i.UpdateId, i.Revision }).OnDelete(DeleteBehavior.Restrict);
    }
}
