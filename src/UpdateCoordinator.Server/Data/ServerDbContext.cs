using Microsoft.EntityFrameworkCore;

namespace UpdateCoordinator.Server.Data;

internal sealed class ServerDbContext(DbContextOptions<ServerDbContext> options) : DbContext(options)
{
    public DbSet<ClientEntity> Clients => Set<ClientEntity>();

    public DbSet<TrustBindingEntity> TrustBindings => Set<TrustBindingEntity>();

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
        client.HasIndex(c => c.HostName);

        var binding = modelBuilder.Entity<TrustBindingEntity>();
        binding.ToTable("TrustBindings");
        binding.HasKey(b => b.Serial);
        binding.Property(b => b.Serial).ValueGeneratedNever();
        binding.Property(b => b.CertificateSpkiSha256).HasMaxLength(32).IsRequired();
        binding.Property(b => b.Signature).HasMaxLength(256).IsRequired();
    }
}
