using Application.Shared.Models;
using Application.Shared.Models.Dashboards;
using Application.Shared.Models.Data;
using Application.Shared.Models.Notebooks;
using Application.Shared.Models.User;
using Application.Shared.Models.WhatsNew;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using System.Text;

namespace Application.Shared.Data
{
    //public delegate ApplicationDbContext DbContextFactory(string companyId);

    public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : DbContext(options)
    {

        // public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options)
        // {
        // }


        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // Configure the model to use snake case naming
            base.OnModelCreating(modelBuilder);

            // add builder to remove the cascadingfor all the foreign keys in all classes
            foreach (var relationship in modelBuilder.Model.GetEntityTypes().SelectMany(e => e.GetForeignKeys()))
            {
                relationship.DeleteBehavior = DeleteBehavior.Restrict;
            }


            foreach (var entity in modelBuilder.Model.GetEntityTypes())
            {


                // Convert table names to snake case
                // check if the entity has Table attribute
                if (entity.GetTableName() != null)
                {
                    entity.SetTableName(ToSnakeCase(entity.GetTableName()));
                }


                // Convert column names to snake case
                foreach (var property in entity.GetProperties())
                {
                    // get the attributes of the property
                    // Console.WriteLine(property);
                    var attributes = property.PropertyInfo.GetCustomAttributesData();

                    // check if the column does not have Column attribute
                    if (!attributes.Any(a => a.AttributeType.Name == "ColumnAttribute"))
                    {
                        property.SetColumnName(ToSnakeCase(property.Name));
                    }
                }
            }
        }





        public DbSet<Company> Company { get; set; }
        
        public DbSet<CompanyMember> CompanyMember { get; set; }

        public DbSet<CompanyDomain> CompanyDomain { get; set; }

        public DbSet<ApplicationUser> ApplicationUser { get; set; }


        // DATA MODELS
        public DbSet<Database> Database { get; set; }
        public DbSet<Dataset> Dataset { get; set; }
        
        public DbSet<DatasetUser> DatasetUser { get; set; }

        public DbSet<DatasetUserTable> DatasetUserTable { get; set; }

        public DbSet<DataTableComment> DataTableComment { get; set; }

        public DbSet<SalesData> SalesData { get; set; }

        // External-access API keys and their per-dataset/table grants.
        public DbSet<ApiKey> ApiKey { get; set; }
        public DbSet<ApiKeyScope> ApiKeyScope { get; set; }

        // Saved SQL queries authored in the Query workbench.
        public DbSet<SavedQuery> SavedQuery { get; set; }

        // Scheduled/automated ingestion sources and their run history.
        public DbSet<IngestionSource> IngestionSource { get; set; }
        public DbSet<IngestionRun> IngestionRun { get; set; }

        // Links a dashboard page to an ingested dataset table (for "view details" deep-links).
        public DbSet<DashboardDataLink> DashboardDataLink { get; set; }

        // Conversationally-built dashboards (AI dashboard builder) and their widgets.
        public DbSet<AiDashboard> AiDashboard { get; set; }
        public DbSet<AiDashboardWidget> AiDashboardWidget { get; set; }

        // Semantic layer: AI-generated + human-edited per-column documentation for dataset tables.
        public DbSet<DatasetColumnDoc> DatasetColumnDoc { get; set; }

        // MotherDuck-style SQL notebooks: ordered cells (SQL/Markdown), each cell picking its own dataset.
        public DbSet<QueryNotebook> QueryNotebook { get; set; }
        public DbSet<QueryNotebookCell> QueryNotebookCell { get; set; }

        // Per-user notebook sharing grants (additive to QueryNotebook.IsShared) and per-cell comment threads.
        public DbSet<NotebookUser> NotebookUser { get; set; }
        public DbSet<NotebookCellComment> NotebookCellComment { get; set; }

        // Append-only run history behind each cell's "run history" panel.
        public DbSet<NotebookCellRun> NotebookCellRun { get; set; }

        // "What's New" feature-announcement feed + per-user last-seen tracking.
        public DbSet<WhatsNewItem> WhatsNewItem { get; set; }
        public DbSet<WhatsNewSeen> WhatsNewSeen { get; set; }

        // Per-company application settings (e.g. the debug-logging toggle).
        public DbSet<CompanySettings> CompanySettings { get; set; }

        // METRICS
        public DbSet<Metric> Metrics { get; set; }

        public DbSet<MetricValue> MetricValues { get; set; }

        public DbSet<MetricTarget> MetricTargets { get; set; }

        public DbSet<MetricFunction> MetricFunctions { get; set; }

        public DbSet<MetricOwner> MetricOwners { get; set; }

        public DbSet<MetricRecipient> MetricRecipients { get; set; }

        public DbSet<MetricVerifier> MetricVerifiers { get; set; }

        public DbSet<MetricDimension> MetricDimensions { get; set; }

        public DbSet<MetricDataSource> MetricDataSources { get; set; }

        public DbSet<MetricFilter> MetricFilters { get; set; }




        /// <summary>
        /// Converts a given string to snake case.
        /// </summary>
        /// <param name="input">The input string to be converted.</param>
        /// <returns>The converted string in snake case.</returns>
        private static string ToSnakeCase(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;

            var stringBuilder = new StringBuilder();
            var previousCharWasUpper = false;

            foreach (var character in input)
            {
                if (char.IsUpper(character))
                {
                    if (stringBuilder.Length != 0 && !previousCharWasUpper)
                    {
                        stringBuilder.Append('_');
                    }
                    stringBuilder.Append(char.ToLowerInvariant(character));
                    previousCharWasUpper = true;
                }
                else
                {
                    stringBuilder.Append(character);
                    previousCharWasUpper = false;
                }
            }

            return stringBuilder.ToString();
        }
    }
}
