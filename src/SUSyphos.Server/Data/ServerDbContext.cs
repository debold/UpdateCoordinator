using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace SUSyphos.Server.Data;

internal sealed class ServerDbContext(DbContextOptions<ServerDbContext> options) : IdentityDbContext<IdentityUser>(options)
{
    public DbSet<ClientEntity> Clients => Set<ClientEntity>();

    public DbSet<TrustBindingEntity> TrustBindings => Set<TrustBindingEntity>();

    public DbSet<UpdateEntity> Updates => Set<UpdateEntity>();

    public DbSet<UpdateSupersedenceEntity> UpdateSupersedence => Set<UpdateSupersedenceEntity>();

    public DbSet<ClientUpdateEntity> ClientUpdates => Set<ClientUpdateEntity>();

    public DbSet<ClientAvailableUpdateEntity> ClientAvailableUpdates => Set<ClientAvailableUpdateEntity>();

    public DbSet<ClientUpdateHistoryEntity> ClientUpdateHistory => Set<ClientUpdateHistoryEntity>();

    public DbSet<SUSyphos.Server.Agents.ClientActivityEntity> ClientActivities => Set<SUSyphos.Server.Agents.ClientActivityEntity>();

    public DbSet<ReleaseEntity> Releases => Set<ReleaseEntity>();

    public DbSet<InstallationEntity> Installations => Set<InstallationEntity>();

    public DbSet<AuditEntryEntity> AuditEntries => Set<AuditEntryEntity>();

    public DbSet<RingEntity> Rings => Set<RingEntity>();

    public DbSet<GroupEntity> Groups => Set<GroupEntity>();

    public DbSet<ClientGroupEntity> ClientGroups => Set<ClientGroupEntity>();

    public DbSet<AssignmentRuleEntity> AssignmentRules => Set<AssignmentRuleEntity>();

    public DbSet<SettingEntity> Settings => Set<SettingEntity>();

    public DbSet<RolloutEntity> Rollouts => Set<RolloutEntity>();

    public DbSet<RolloutRingEntity> RolloutRings => Set<RolloutRingEntity>();

    public DbSet<RingTransitionEntity> RingTransitions => Set<RingTransitionEntity>();

    public DbSet<StartRuleEntity> StartRules => Set<StartRuleEntity>();

    public DbSet<ComplianceSnapshotEntity> ComplianceSnapshots => Set<ComplianceSnapshotEntity>();

    public DbSet<AgentActionEntity> AgentActions => Set<AgentActionEntity>();

    public DbSet<LeaseEntity> Leases => Set<LeaseEntity>();

    public DbSet<SUSyphos.Server.Approval.DirectoryComputerEntity> DirectoryComputers => Set<SUSyphos.Server.Approval.DirectoryComputerEntity>();

    public DbSet<SUSyphos.Server.Notifications.NotificationSentEntity> NotificationsSent => Set<SUSyphos.Server.Notifications.NotificationSentEntity>();

    public DbSet<SUSyphos.Server.AgentPackages.AgentPackageEntity> AgentPackages => Set<SUSyphos.Server.AgentPackages.AgentPackageEntity>();

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

        var ring = modelBuilder.Entity<RingEntity>();
        ring.ToTable("Rings");
        ring.HasKey(r => r.Id);
        ring.Property(r => r.Id).ValueGeneratedNever();
        ring.Property(r => r.Name).HasMaxLength(64).IsRequired();
        ring.HasData(
            new RingEntity { Id = RingEntity.PilotId, Name = "Pilot", Order = 1 },
            new RingEntity { Id = RingEntity.TestId, Name = "Test", Order = 2 },
            new RingEntity { Id = RingEntity.ProductionId, Name = "Production", Order = 3, IsDefault = true });

        var transition = modelBuilder.Entity<RingTransitionEntity>();
        transition.ToTable("RingTransitions");
        transition.HasKey(t => t.FromRingId);
        transition.Property(t => t.FromRingId).ValueGeneratedNever();
        transition.HasOne<RingEntity>().WithMany().HasForeignKey(t => t.FromRingId).OnDelete(DeleteBehavior.Cascade);
        transition.HasData(
            new RingTransitionEntity
            {
                FromRingId = RingEntity.PilotId,
                MinimumWait = TimeSpan.FromDays(2),
                MaximumWait = TimeSpan.FromDays(7),
                MinimumFeedbackPercent = 80,
                MinimumSuccessPercent = 95,
                ManualGate = false,
            },
            new RingTransitionEntity
            {
                FromRingId = RingEntity.TestId,
                MinimumWait = TimeSpan.FromDays(3),
                MaximumWait = TimeSpan.FromDays(10),
                MinimumFeedbackPercent = 80,
                MinimumSuccessPercent = 95,
                ManualGate = true,
            });

        var startRule = modelBuilder.Entity<StartRuleEntity>();
        startRule.ToTable("StartRules");
        startRule.HasKey(r => r.Id);
        startRule.Property(r => r.Mode).HasConversion<int>();
        startRule.Property(r => r.Classification).HasMaxLength(64);
        startRule.Property(r => r.MsrcSeverity).HasMaxLength(32);
        startRule.Property(r => r.StartRingIds).HasMaxLength(200);
        startRule.Property(r => r.TitlePattern).HasMaxLength(128);

        // Default: every update starts automatically and without delay (decided 2026-10-18).
        startRule.HasData(new StartRuleEntity { Id = StartRuleEntity.CatchAllId, Priority = 1000, Mode = StartMode.Automatic, Delay = TimeSpan.Zero });

        var computer = modelBuilder.Entity<SUSyphos.Server.Approval.DirectoryComputerEntity>();
        computer.ToTable("DirectoryComputers");
        computer.HasKey(c => c.Name);
        computer.Property(c => c.Name).HasMaxLength(64).UseCollation("NOCASE");
        computer.Property(c => c.DnsHostName).HasMaxLength(255);
        computer.Property(c => c.OperatingSystem).HasMaxLength(128);

        var notification = modelBuilder.Entity<SUSyphos.Server.Notifications.NotificationSentEntity>();
        notification.ToTable("NotificationsSent");
        notification.HasKey(n => n.Key);
        notification.Property(n => n.Key).HasMaxLength(SUSyphos.Server.Notifications.NotificationSentEntity.MaxKeyLength);

        var package = modelBuilder.Entity<SUSyphos.Server.AgentPackages.AgentPackageEntity>();
        package.ToTable("AgentPackages");
        package.HasKey(p => p.Version);
        package.Property(p => p.Version).HasMaxLength(AgentVersion.MaxLength);
        package.Property(p => p.FileName).HasMaxLength(128);
        package.Property(p => p.Sha256).HasMaxLength(64);
        package.Property(p => p.ReleasedBy).HasMaxLength(256);
        package.Property(p => p.Source).HasConversion<int>();

        var lease = modelBuilder.Entity<LeaseEntity>();
        lease.ToTable("Leases");
        lease.HasKey(l => l.Id);
        lease.Property(l => l.State).HasConversion<int>();
        lease.HasIndex(l => new { l.GroupId, l.State });
        lease.HasIndex(l => new { l.ClientId, l.State });
        lease.HasOne<GroupEntity>().WithMany().HasForeignKey(l => l.GroupId).OnDelete(DeleteBehavior.Cascade);
        lease.HasOne<ClientEntity>().WithMany().HasForeignKey(l => l.ClientId).OnDelete(DeleteBehavior.Cascade);

        var action = modelBuilder.Entity<AgentActionEntity>();
        action.ToTable("AgentActions");
        action.HasKey(a => a.Id);
        action.Property(a => a.Id).ValueGeneratedNever();
        action.Property(a => a.KbArticle).HasMaxLength(10);
        action.Property(a => a.Kind).HasConversion<int>();
        action.Property(a => a.State).HasConversion<int>();
        action.Property(a => a.RequestedBy).HasMaxLength(256).IsRequired();
        action.Property(a => a.Detail).HasMaxLength(512);
        action.HasIndex(a => new { a.ClientId, a.State });
        action.HasOne<ClientEntity>().WithMany().HasForeignKey(a => a.ClientId).OnDelete(DeleteBehavior.Cascade);

        var compliance = modelBuilder.Entity<ComplianceSnapshotEntity>();
        compliance.ToTable("ComplianceSnapshots");
        compliance.HasKey(s => s.Day);

        var rollout = modelBuilder.Entity<RolloutEntity>();
        rollout.ToTable("Rollouts");
        rollout.HasKey(r => new { r.UpdateId, r.Revision });
        rollout.Property(r => r.State).HasConversion<int>();
        rollout.Property(r => r.StartedBy).HasMaxLength(256).IsRequired();
        rollout.Property(r => r.StartRuleSummary).HasMaxLength(512);
        rollout.HasOne<UpdateEntity>().WithMany().HasForeignKey(r => new { r.UpdateId, r.Revision }).OnDelete(DeleteBehavior.Restrict);

        var rolloutRing = modelBuilder.Entity<RolloutRingEntity>();
        rolloutRing.ToTable("RolloutRings");
        rolloutRing.HasKey(r => new { r.UpdateId, r.Revision, r.RingId });
        rolloutRing.Property(r => r.State).HasConversion<int>();
        rolloutRing.Property(r => r.Reason).HasMaxLength(256);
        rolloutRing.HasIndex(r => r.State);
        rolloutRing.HasOne<RolloutEntity>().WithMany().HasForeignKey(r => new { r.UpdateId, r.Revision }).OnDelete(DeleteBehavior.Cascade);
        rolloutRing.HasOne<RingEntity>().WithMany().HasForeignKey(r => r.RingId).OnDelete(DeleteBehavior.Cascade);

        var group = modelBuilder.Entity<GroupEntity>();
        group.ToTable("Groups");
        group.HasKey(g => g.Id);
        group.Property(g => g.Name).HasMaxLength(64).IsRequired();
        group.Property(g => g.Description).HasMaxLength(256);
        group.Property(g => g.MaintenanceWindowsJson).HasMaxLength(4096);
        group.HasIndex(g => g.Name).IsUnique();
        group.Property(g => g.LeasePercent).HasDefaultValue(25);
        group.Property(g => g.LeaseMaximum).HasDefaultValue(5);

        var membership = modelBuilder.Entity<ClientGroupEntity>();
        membership.ToTable("ClientGroups");
        membership.HasKey(m => new { m.ClientId, m.GroupId });
        membership.HasOne<ClientEntity>().WithMany().HasForeignKey(m => m.ClientId).OnDelete(DeleteBehavior.Cascade);
        membership.HasOne<GroupEntity>().WithMany().HasForeignKey(m => m.GroupId).OnDelete(DeleteBehavior.Cascade);

        var rule = modelBuilder.Entity<AssignmentRuleEntity>();
        rule.ToTable("AssignmentRules");
        rule.HasKey(r => r.Id);
        rule.Property(r => r.Target).HasConversion<int>();
        rule.Property(r => r.Role).HasMaxLength(700);
        rule.Property(r => r.Domain).HasMaxLength(253);
        rule.Property(r => r.HostNamePattern).HasMaxLength(63);

        var setting = modelBuilder.Entity<SettingEntity>();
        setting.ToTable("Settings");
        setting.HasKey(s => s.Key);
        setting.Property(s => s.Key).HasMaxLength(128);
        setting.Property(s => s.Value).HasMaxLength(8192).IsRequired();

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
        client.Property(c => c.AgentUpdateError).HasMaxLength(512);
        client.Property(c => c.RegisteredFromAddress).HasMaxLength(45);
        client.Property(c => c.OsProductName).HasMaxLength(128);
        client.Property(c => c.OsEditionId).HasMaxLength(64);
        client.Property(c => c.OsDisplayVersion).HasMaxLength(16);
        client.Property(c => c.WsusServer).HasMaxLength(255);
        client.Property(c => c.TimeZoneId).HasMaxLength(64);
        client.Property(c => c.LastHeartbeatHostName).HasMaxLength(63);
        client.Property(c => c.PreviousHostName).HasMaxLength(63);
        client.OwnsMany(c => c.HealthChecks, health => health.ToJson());
        client.Property(c => c.MaintenanceWindowsJson).HasMaxLength(4096);
        client.Property(c => c.EffectiveWindowsJson).HasMaxLength(8192);
        client.HasOne<RingEntity>().WithMany().HasForeignKey(c => c.RingId).OnDelete(DeleteBehavior.SetNull);
        client.HasOne<RingEntity>().WithMany().HasForeignKey(c => c.ManualRingId).OnDelete(DeleteBehavior.SetNull);
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
        update.Property(u => u.BlockedBy).HasMaxLength(256);
        update.HasIndex(u => u.KbArticle);

        var supersedence = modelBuilder.Entity<UpdateSupersedenceEntity>();
        supersedence.ToTable("UpdateSupersedence");
        supersedence.HasKey(s => new { s.UpdateId, s.Revision, s.SupersededUpdateId });
        supersedence.HasOne<UpdateEntity>().WithMany().HasForeignKey(s => new { s.UpdateId, s.Revision }).OnDelete(DeleteBehavior.Cascade);
        supersedence.HasIndex(s => s.SupersededUpdateId);

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

        var activity = modelBuilder.Entity<SUSyphos.Server.Agents.ClientActivityEntity>();
        activity.ToTable("ClientActivities");
        activity.HasKey(a => a.Id);
        activity.Property(a => a.Detail).HasMaxLength(SUSyphos.Server.Agents.ClientActivityLog.MaxDetailLength).IsRequired();
        activity.HasIndex(a => new { a.ClientId, a.AtUtc });
        activity.HasIndex(a => a.AtUtc);
        activity.HasOne<ClientEntity>().WithMany().HasForeignKey(a => a.ClientId).OnDelete(DeleteBehavior.Cascade);

        var history = modelBuilder.Entity<ClientUpdateHistoryEntity>();
        history.ToTable("ClientUpdateHistory");
        history.HasKey(h => h.Id);
        history.Property(h => h.Title).HasMaxLength(512).IsRequired();
        history.Property(h => h.ClientApplicationId).HasMaxLength(128);
        history.HasIndex(h => new { h.ClientId, h.DateUtc });
        history.HasOne<ClientEntity>().WithMany().HasForeignKey(h => h.ClientId).OnDelete(DeleteBehavior.Cascade);

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
