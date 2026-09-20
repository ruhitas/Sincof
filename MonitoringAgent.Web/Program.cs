using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllersWithViews();

builder.Services.AddDbContext<MonitoringAgent.Web.Data.MonitoringDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")
        ?? "Server=(localdb)\\MSSQLLocalDB;Database=MonitoringAgentDb;Trusted_Connection=True;MultipleActiveResultSets=true;TrustServerCertificate=True"));


builder.Services.AddControllers()
    .AddJsonOptions(options => {
        options.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    });

var app = builder.Build();

// Ensure Database is Created
using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
        try
        {
            var context = services.GetRequiredService<MonitoringAgent.Web.Data.MonitoringDbContext>();
            context.Database.EnsureCreated();

            // Automatic Migration: Add missing tables/columns if DB already existed
            var db = context.Database;
            db.ExecuteSqlRaw(@"
                -- InstanceMetrics Updates
                IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID(N'[InstanceMetrics]') AND name = 'SqlCpuUsagePercent')
                    ALTER TABLE [InstanceMetrics] ADD [SqlCpuUsagePercent] float NOT NULL DEFAULT 0;
                IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID(N'[InstanceMetrics]') AND name = 'SqlMemoryUsageMb')
                    ALTER TABLE [InstanceMetrics] ADD [SqlMemoryUsageMb] float NOT NULL DEFAULT 0;
                IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID(N'[InstanceMetrics]') AND name = 'ProviderType')
                    ALTER TABLE [InstanceMetrics] ADD [ProviderType] nvarchar(max) NOT NULL DEFAULT 'SqlServer';
                IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID(N'[InstanceMetrics]') AND name = 'DatabaseIoPercent')
                    ALTER TABLE [InstanceMetrics] ADD [DatabaseIoPercent] float NOT NULL DEFAULT 0;
                IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID(N'[InstanceMetrics]') AND name = 'LogWritePercent')
                    ALTER TABLE [InstanceMetrics] ADD [LogWritePercent] float NOT NULL DEFAULT 0;
                IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID(N'[InstanceMetrics]') AND name = 'WorkerUtilizationPercent')
                    ALTER TABLE [InstanceMetrics] ADD [WorkerUtilizationPercent] float NOT NULL DEFAULT 0;
                IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID(N'[InstanceMetrics]') AND name = 'SessionUtilizationPercent')
                    ALTER TABLE [InstanceMetrics] ADD [SessionUtilizationPercent] float NOT NULL DEFAULT 0;
                IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID(N'[InstanceMetrics]') AND name = 'AzureServiceTier')
                    ALTER TABLE [InstanceMetrics] ADD [AzureServiceTier] nvarchar(max) NOT NULL DEFAULT '';

                -- ProviderType for other tables
                IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID(N'[DatabaseMetrics]') AND name = 'ProviderType')
                    ALTER TABLE [DatabaseMetrics] ADD [ProviderType] nvarchar(max) NOT NULL DEFAULT 'SqlServer';
                IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID(N'[Alerts]') AND name = 'ProviderType')
                    ALTER TABLE [Alerts] ADD [ProviderType] nvarchar(max) NOT NULL DEFAULT 'SqlServer';
                IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID(N'[Queries]') AND name = 'ProviderType')
                    ALTER TABLE [Queries] ADD [ProviderType] nvarchar(max) NOT NULL DEFAULT 'SqlServer';
                IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID(N'[IndexIssues]') AND name = 'ProviderType')
                    ALTER TABLE [IndexIssues] ADD [ProviderType] nvarchar(max) NOT NULL DEFAULT 'SqlServer';
                IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID(N'[BlockingEvents]') AND name = 'ProviderType')
                    ALTER TABLE [BlockingEvents] ADD [ProviderType] nvarchar(max) NOT NULL DEFAULT 'SqlServer';
                IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID(N'[DeadlockEvents]') AND name = 'ProviderType')
                    ALTER TABLE [DeadlockEvents] ADD [ProviderType] nvarchar(max) NOT NULL DEFAULT 'SqlServer';

                -- Update WorkerConfigs if existing
                IF EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[WorkerConfigs]') AND type in (N'U'))
                BEGIN
                    IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID(N'[WorkerConfigs]') AND name = 'IndexAnalysisJson')
                        ALTER TABLE [WorkerConfigs] ADD [IndexAnalysisJson] nvarchar(max) NOT NULL DEFAULT '';
                    IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID(N'[WorkerConfigs]') AND name = 'AdvancedControlsJson')
                        ALTER TABLE [WorkerConfigs] ADD [AdvancedControlsJson] nvarchar(max) NOT NULL DEFAULT '';
                    IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID(N'[WorkerConfigs]') AND name = 'LoggingJson')
                        ALTER TABLE [WorkerConfigs] ADD [LoggingJson] nvarchar(max) NOT NULL DEFAULT '';
                END

                -- New Table: WorkerConfigs
                IF OBJECT_ID(N'[WorkerConfigs]', N'U') IS NULL
                BEGIN
                    CREATE TABLE [WorkerConfigs] (
                        [Id] int NOT NULL IDENTITY,
                        [WorkerId] nvarchar(max) NOT NULL DEFAULT '',
                        [WorkerName] nvarchar(max) NOT NULL DEFAULT '',
                        [IncludeDBs] nvarchar(max) NOT NULL DEFAULT '',
                        [ExcludeDBs] nvarchar(max) NOT NULL DEFAULT '',
                        [IntervalsJson] nvarchar(max) NOT NULL DEFAULT '',
                        [FeatureTogglesJson] nvarchar(max) NOT NULL DEFAULT '',
                        [ThresholdsJson] nvarchar(max) NOT NULL DEFAULT '',
                        [ApiSettingsJson] nvarchar(max) NOT NULL DEFAULT '',
                        [IndexAnalysisJson] nvarchar(max) NOT NULL DEFAULT '',
                        [AdvancedControlsJson] nvarchar(max) NOT NULL DEFAULT '',
                        [LoggingJson] nvarchar(max) NOT NULL DEFAULT '',
                        [LastUpdated] datetime2 NOT NULL DEFAULT '2026-01-01',
                        CONSTRAINT [PK_WorkerConfigs] PRIMARY KEY ([Id])
                    );
                END

                -- New Table: TargetServers
                IF OBJECT_ID(N'[TargetServers]', N'U') IS NULL
                BEGIN
                    CREATE TABLE [TargetServers] (
                        [Id] int NOT NULL IDENTITY,
                        [WorkerConfigId] int NOT NULL,
                        [Alias] nvarchar(max) NOT NULL DEFAULT '',
                        [ProviderType] nvarchar(max) NOT NULL DEFAULT 'SqlServer',
                        [ConnectionString] nvarchar(max) NOT NULL DEFAULT '',
                        [IncludeDBs] nvarchar(max) NOT NULL DEFAULT '',
                        [IsEnabled] bit NOT NULL DEFAULT 1,
                        CONSTRAINT [PK_TargetServers] PRIMARY KEY ([Id])
                    );
                END

                -- New Table: DiskTestLogs
                IF OBJECT_ID(N'[DiskTestLogs]', N'U') IS NULL
                BEGIN
                    CREATE TABLE [DiskTestLogs] (
                        [Id] int NOT NULL IDENTITY,
                        [RequestId] nvarchar(max) NOT NULL DEFAULT '',
                        [ServerName] nvarchar(max) NOT NULL DEFAULT '',
                        [TestPath] nvarchar(max) NOT NULL DEFAULT '',
                        [IOPS] float NOT NULL DEFAULT 0,
                        [MBps] float NOT NULL DEFAULT 0,
                        [LatencyMs] float NOT NULL DEFAULT 0,
                        [CpuUsagePercent] float NOT NULL DEFAULT 0,
                        [FileSizeGB] int NOT NULL DEFAULT 0,
                        [DurationSec] int NOT NULL DEFAULT 0,
                        [WritePercent] int NOT NULL DEFAULT 0,
                        [BlockSizeKB] int NOT NULL DEFAULT 0,
                        [Threads] int NOT NULL DEFAULT 0,
                        [QueueDepth] int NOT NULL DEFAULT 0,
                        [RawOutput] nvarchar(max) NOT NULL DEFAULT '',
                        [Success] bit NOT NULL DEFAULT 0,
                        [ErrorMessage] nvarchar(max) NOT NULL DEFAULT '',
                        [TestTime] datetime2 NOT NULL DEFAULT '2026-01-01',
                        CONSTRAINT [PK_DiskTestLogs] PRIMARY KEY ([Id])
                    );
                END
            ");
        }
        catch (Exception ex)
        {
            var logger = services.GetRequiredService<ILogger<Program>>();
            logger.LogError(ex, "An error occurred creating or updating the DB.");
        }
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();

app.UseAuthorization();

app.UseStaticFiles();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();
