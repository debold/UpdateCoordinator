using Microsoft.EntityFrameworkCore;

namespace UpdateCoordinator.Server.Data;

internal sealed class ServerDbContext(DbContextOptions<ServerDbContext> options) : DbContext(options)
{
    public DbSet<ClientEntity> Clients => Set<ClientEntity>();

    public DbSet<TrustBindingEntity> TrustBindings => Set<TrustBindingEntity>();

    public DbSet<UpdateEntity> Updates => Set<UpdateEntity>();

    public DbSet<ClientUpdateEntity> ClientUpdates => Set<ClientUpdateEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
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
        update.HasIndex(u => u.KbArticle);

        var clientUpdate = modelBuilder.Entity<ClientUpdateEntity>();
        clientUpdate.ToTable("ClientUpdates");
        clientUpdate.HasKey(cu => new { cu.ClientId, cu.UpdateId, cu.Revision });
        clientUpdate.HasOne<ClientEntity>().WithMany().HasForeignKey(cu => cu.ClientId).OnDelete(DeleteBehavior.Cascade);
        clientUpdate.HasOne<UpdateEntity>().WithMany().HasForeignKey(cu => new { cu.UpdateId, cu.Revision }).OnDelete(DeleteBehavior.Restrict);
    }
}
