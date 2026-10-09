using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace DeveAgentManager.Core.Data;

public class ManagerDbContext(DbContextOptions<ManagerDbContext> options) : DbContext(options)
{
    public DbSet<Runner> Runners => Set<Runner>();

    public DbSet<Agent> Agents => Set<Agent>();

    public DbSet<SettingEntry> Settings => Set<SettingEntry>();

    public DbSet<StoredSession> Sessions => Set<StoredSession>();

    public DbSet<Account> Accounts => Set<Account>();

    public DbSet<RunnerActivity> RunnerActivities => Set<RunnerActivity>();

    public DbSet<AgentIcon> AgentIcons => Set<AgentIcon>();
    public DbSet<AgentAutoContinue> AgentAutoContinues => Set<AgentAutoContinue>();
    public DbSet<AgentLimit> AgentLimits => Set<AgentLimit>();

    public DbSet<User> Users => Set<User>();

    public DbSet<UserSetting> UserSettings => Set<UserSetting>();

    public DbSet<RunnerMember> RunnerMembers => Set<RunnerMember>();

    public DbSet<AgentShare> AgentShares => Set<AgentShare>();

    public DbSet<SessionShare> SessionShares => Set<SessionShare>();

    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    public DbSet<SignInSession> SignInSessions => Set<SignInSession>();
    public DbSet<UserSecondFactor> UserSecondFactors => Set<UserSecondFactor>();
    public DbSet<EditorTicket> EditorTickets => Set<EditorTicket>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // SQLite has no date type: store UTC and give values back as UTC.
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        configurationBuilder.Properties<DateTime?>().HaveConversion<NullableUtcDateTimeConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SignInSession>(session =>
        {
            session.HasKey(s => s.Id);
            session.HasIndex(s => s.UserId);
            session.HasOne<User>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<UserSecondFactor>(factor =>
        {
            factor.HasKey(s => s.UserId);
            factor.HasOne<User>().WithOne().HasForeignKey<UserSecondFactor>(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<EditorTicket>(ticket =>
        {
            ticket.HasKey(t => t.Id);
            ticket.HasOne<SignInSession>().WithMany().HasForeignKey(t => t.SessionId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<AgentAutoContinue>(policy =>
        {
            policy.HasKey(p => p.AgentId);
            policy.HasOne<Agent>().WithOne().HasForeignKey<AgentAutoContinue>(p => p.AgentId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<AgentLimit>(limit =>
        {
            limit.HasKey(l => l.AgentId);
            limit.HasOne<Agent>().WithOne().HasForeignKey<AgentLimit>(l => l.AgentId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<Runner>(runner =>
        {
            runner.HasKey(r => r.Id);
            runner.HasIndex(r => r.Name).IsUnique();
            runner.Property(r => r.Name).HasMaxLength(63);
            runner.Property(r => r.State).HasConversion<string>().HasMaxLength(32);
            runner.Property(r => r.Kind).HasConversion<string>().HasMaxLength(32);
            runner.Ignore(r => r.HarnessIds);
            runner.Ignore(r => r.ToolIds);
            runner.HasMany(r => r.Agents).WithOne(a => a.Runner).HasForeignKey(a => a.RunnerId).OnDelete(DeleteBehavior.Cascade);
            // Users who own something can't be deleted before it has another owner (see UserService.DeleteAsync).
            runner.HasOne(r => r.Owner).WithMany().HasForeignKey(r => r.OwnerId).OnDelete(DeleteBehavior.Restrict);
            runner.HasMany(r => r.Members).WithOne().HasForeignKey(m => m.RunnerId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RunnerMember>(member =>
        {
            member.HasKey(m => new { m.RunnerId, m.UserId });
            member.HasIndex(m => m.UserId);
            member.Property(m => m.Access).HasConversion<string>().HasMaxLength(32);
            member.HasOne(m => m.User).WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RunnerActivity>(activity =>
        {
            activity.HasKey(a => a.Id);
            activity.HasIndex(a => new { a.RunnerId, a.StartedAt });
            activity.Property(a => a.Name).HasMaxLength(200);
            activity.Property(a => a.Outcome).HasConversion<string>().HasMaxLength(32);
            activity.HasOne<Runner>().WithMany().HasForeignKey(a => a.RunnerId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AgentIcon>(icon =>
        {
            icon.HasKey(i => i.AgentId);
            icon.Property(i => i.Path).HasMaxLength(1024);
            icon.Property(i => i.ContentType).HasMaxLength(64);
            icon.HasOne<Agent>().WithOne().HasForeignKey<AgentIcon>(i => i.AgentId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Agent>(agent =>
        {
            agent.HasKey(a => a.Id);
            agent.HasIndex(a => new { a.RunnerId, a.Name }).IsUnique();
            agent.Property(a => a.State).HasConversion<string>().HasMaxLength(32);
            agent.HasOne(a => a.Account).WithMany().HasForeignKey(a => a.AccountId).OnDelete(DeleteBehavior.SetNull);
            agent.HasOne(a => a.Owner).WithMany().HasForeignKey(a => a.OwnerId).OnDelete(DeleteBehavior.Restrict);
            agent.HasMany(a => a.Shares).WithOne().HasForeignKey(s => s.AgentId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AgentShare>(share =>
        {
            share.HasKey(s => new { s.AgentId, s.UserId });
            share.HasIndex(s => s.UserId);
            share.Property(s => s.Access).HasConversion<string>().HasMaxLength(32);
            share.HasOne(s => s.User).WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Account>(account =>
        {
            account.HasKey(a => a.Id);
            account.HasIndex(a => new { a.OwnerId, a.HarnessId });
            account.Property(a => a.Kind).HasConversion<string>().HasMaxLength(32);
            account.HasOne(a => a.Owner).WithMany().HasForeignKey(a => a.OwnerId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<StoredSession>(session =>
        {
            session.HasKey(s => s.Id);
            session.HasIndex(s => s.AgentId).IsUnique();
            session.HasOne(s => s.WorkspaceAgent).WithMany().HasForeignKey(s => s.WorkspaceAgentId).OnDelete(DeleteBehavior.SetNull);
            session.HasOne(s => s.Agent).WithMany().HasForeignKey(s => s.AgentId).OnDelete(DeleteBehavior.SetNull);
            session.HasOne(s => s.Owner).WithMany().HasForeignKey(s => s.OwnerId).OnDelete(DeleteBehavior.Restrict);
            session.HasMany(s => s.Shares).WithOne().HasForeignKey(s => s.SessionId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SessionShare>(share =>
        {
            share.HasKey(s => new { s.SessionId, s.UserId });
            share.HasIndex(s => s.UserId);
            share.HasOne(s => s.User).WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<User>(user =>
        {
            user.HasKey(u => u.Id);
            user.Property(u => u.Username).HasMaxLength(64).UseCollation("NOCASE");
            user.HasIndex(u => u.Username).IsUnique();
            user.Property(u => u.DisplayName).HasMaxLength(100);
            user.Property(u => u.Roles).HasConversion<int>();
            user.HasData(new User
            {
                Id = User.BootstrapId,
                Username = "admin",
                DisplayName = "Admin",
                SecurityStamp = "bootstrap",
                Roles = UserRoles.Admin,
                CreatedAt = new DateTime(2026, 10, 6, 0, 0, 0, DateTimeKind.Utc),
                UpdatedAt = new DateTime(2026, 10, 6, 0, 0, 0, DateTimeKind.Utc),
            });
        });

        modelBuilder.Entity<UserSetting>(setting =>
        {
            setting.HasKey(s => new { s.UserId, s.Key });
            setting.HasOne<User>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AuditEvent>(audit =>
        {
            audit.HasKey(a => a.Id);
            audit.HasIndex(a => a.At);
            audit.Property(a => a.Action).HasMaxLength(64);
            audit.Property(a => a.ActorName).HasMaxLength(64);
            audit.Property(a => a.Target).HasMaxLength(200);
        });

        modelBuilder.Entity<SettingEntry>(setting =>
        {
            setting.HasKey(s => s.Key);
        });
    }

    private sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
        v => v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime(),
        v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

    private sealed class NullableUtcDateTimeConverter() : ValueConverter<DateTime?, DateTime?>(
        v => v.HasValue ? (v.Value.Kind == DateTimeKind.Utc ? v.Value : v.Value.ToUniversalTime()) : v,
        v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);
}

/// <summary>Used by `dotnet ef` to create migrations.</summary>
public sealed class ManagerDbContextDesignTimeFactory : IDesignTimeDbContextFactory<ManagerDbContext>
{
    public ManagerDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<ManagerDbContext>().UseSqlite("Data Source=design-time.db").Options);
}
