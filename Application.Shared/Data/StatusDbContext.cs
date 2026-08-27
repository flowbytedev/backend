using Application.Shared.Models;
using Microsoft.EntityFrameworkCore;
using System.Text;

namespace Application.Shared.Data;

public class StatusDbContext(DbContextOptions<StatusDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // AssetDependency has two FKs to MonitoredAsset — must be explicit or EF Core cannot resolve them.
        modelBuilder.Entity<AssetDependency>()
            .HasOne(d => d.Entity)
            .WithMany(e => e.Dependencies)
            .HasForeignKey(d => d.EntityId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<AssetDependency>()
            .HasOne(d => d.DependsOnEntity)
            .WithMany(e => e.DependentOn)
            .HasForeignKey(d => d.DependsOnEntityId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<EntityAudience>()
            .HasOne(a => a.Entity)
            .WithMany(e => e.Audiences)
            .HasForeignKey(a => a.EntityId)
            .OnDelete(DeleteBehavior.Restrict);

        // A Power BI dataset link points at both an entity and a connection.
        modelBuilder.Entity<PowerBiDatasetLink>()
            .HasOne(l => l.Entity)
            .WithMany()
            .HasForeignKey(l => l.EntityId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<PowerBiDatasetLink>()
            .HasOne(l => l.Connection)
            .WithMany(c => c.DatasetLinks)
            .HasForeignKey(l => l.PowerBiConnectionId)
            .OnDelete(DeleteBehavior.Restrict);

        // A database connection belongs to a single Database entity.
        modelBuilder.Entity<DatabaseConnection>()
            .HasOne(c => c.Entity)
            .WithMany()
            .HasForeignKey(c => c.EntityId)
            .OnDelete(DeleteBehavior.Restrict);

        // A table-freshness check belongs to a single Table entity.
        modelBuilder.Entity<TableCheck>()
            .HasOne(c => c.Entity)
            .WithMany()
            .HasForeignKey(c => c.EntityId)
            .OnDelete(DeleteBehavior.Restrict);

        foreach (var relationship in modelBuilder.Model.GetEntityTypes().SelectMany(e => e.GetForeignKeys()))
        {
            relationship.DeleteBehavior = DeleteBehavior.Restrict;
        }

        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            if (entity.GetTableName() != null)
                entity.SetTableName(ToSnakeCase(entity.GetTableName()));

            foreach (var property in entity.GetProperties())
            {
                var attributes = property.PropertyInfo?.GetCustomAttributesData();
                if (attributes != null && !attributes.Any(a => a.AttributeType.Name == "ColumnAttribute"))
                    property.SetColumnName(ToSnakeCase(property.Name));
            }
        }
    }

    // MONITORING MODULE
    public DbSet<MonitoredAsset> MonitoredAssets { get; set; }
    public DbSet<AssetDependency> AssetDependencies { get; set; }
    public DbSet<EntityAudience> EntityAudiences { get; set; }
    public DbSet<AssetStatusHistory> AssetStatusHistory { get; set; }
    public DbSet<Incident> Incidents { get; set; }
    public DbSet<IncidentUpdate> IncidentUpdates { get; set; }
    public DbSet<MonitoringJob> MonitoringJobs { get; set; }
    public DbSet<MonitoringJobExecution> MonitoringJobExecutions { get; set; }
    public DbSet<AlertRule> AlertRules { get; set; }
    public DbSet<AlertInstance> AlertInstances { get; set; }
    public DbSet<MonitoringPage> MonitoringPages { get; set; }
    public DbSet<MonitoringPageAsset> MonitoringPageAssets { get; set; }

    // SERVER MANAGEMENT
    public DbSet<ServerCredential> ServerCredentials { get; set; }

    // POWER BI
    public DbSet<PowerBiConnection> PowerBiConnections { get; set; }
    public DbSet<PowerBiDatasetLink> PowerBiDatasetLinks { get; set; }

    // DATABASE TABLE DISCOVERY
    public DbSet<DatabaseConnection> DatabaseConnections { get; set; }

    // DATABASE ADMINISTRATION (elevated credential, separate from the read-only connection above)
    public DbSet<DatabaseAdminCredential> DatabaseAdminCredentials { get; set; }

    // The write-side credential for an ETL pipeline destination. Separate from both the read-only
    // connection and the DBA credential — see DatabaseWriteCredential for why.
    public DbSet<DatabaseWriteCredential> DatabaseWriteCredentials { get; set; }

    // TABLE FRESHNESS CHECKS
    public DbSet<TableCheck> TableChecks { get; set; }

    private static string ToSnakeCase(string? input)
    {
        if (string.IsNullOrEmpty(input)) return input ?? string.Empty;

        var sb = new StringBuilder();
        var prevUpper = false;

        foreach (var ch in input)
        {
            if (char.IsUpper(ch))
            {
                if (sb.Length != 0 && !prevUpper)
                    sb.Append('_');
                sb.Append(char.ToLowerInvariant(ch));
                prevUpper = true;
            }
            else
            {
                sb.Append(ch);
                prevUpper = false;
            }
        }

        return sb.ToString();
    }
}
