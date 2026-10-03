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

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // SQLite has no date type: store UTC and give values back as UTC.
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        configurationBuilder.Properties<DateTime?>().HaveConversion<NullableUtcDateTimeConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
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
        });

        modelBuilder.Entity<Account>(account =>
        {
            account.HasKey(a => a.Id);
            account.HasIndex(a => a.HarnessId);
            account.Property(a => a.Kind).HasConversion<string>().HasMaxLength(32);
        });

        modelBuilder.Entity<StoredSession>(session =>
        {
            session.HasKey(s => s.Id);
            session.HasIndex(s => s.AgentId);
            session.HasOne(s => s.Agent).WithMany().HasForeignKey(s => s.AgentId).OnDelete(DeleteBehavior.SetNull);
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
