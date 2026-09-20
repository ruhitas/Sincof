using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Events;
using MonitoringAgent.Application.Services;
using MonitoringAgent.Core.Configuration;
using MonitoringAgent.Core.Interfaces;
using MonitoringAgent.Infrastructure.Sql;
using MonitoringAgent.Infrastructure.Api;
using MonitoringAgent.Infrastructure.Disk;
using MonitoringAgent.Infrastructure;

namespace MonitoringAgent.Worker
{
    public class Program
    {
        public static int Main(string[] args)
        {
            // ── Bootstrap Serilog early so startup errors are captured ──
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                .MinimumLevel.Override("System", LogEventLevel.Warning)
                .Enrich.FromLogContext()
                .Enrich.WithEnvironmentName()
                .Enrich.WithMachineName()
                .WriteTo.Console(outputTemplate:
                    "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
                .WriteTo.File(
                    path: "Logs/monitoring-.log",
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 31,
                    outputTemplate:
                        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
                .CreateBootstrapLogger();

            try
            {
                Log.Information("Starting MonitoringAgent.Worker host...");
                CreateHostBuilder(args).Build().Run();
                Log.Information("MonitoringAgent.Worker host stopped cleanly.");
                return 0;
            }
            catch (System.Exception ex)
            {
                Log.Fatal(ex, "Host terminated unexpectedly");
                return 1;
            }
            finally
            {
                Log.CloseAndFlush();
            }
        }

        public static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .ConfigureAppConfiguration((ctx, config) =>
                {
                    var env = ctx.HostingEnvironment.EnvironmentName;
                    config.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                          .AddJsonFile($"appsettings.{env}.json", optional: true, reloadOnChange: true)
                          .AddJsonFile("appsettings.SqlServer.json", optional: true, reloadOnChange: true)
                          .AddJsonFile("appsettings.Postgres.json", optional: true, reloadOnChange: true)
                          .AddJsonFile("appsettings.Oracle.json", optional: true, reloadOnChange: true)
                          .AddJsonFile("appsettings.MySql.json", optional: true, reloadOnChange: true)
                          .AddJsonFile("appsettings.AzureSql.json", optional: true, reloadOnChange: true);
                })
                // ── Serilog replaces Microsoft's default logging ──
                .UseSerilog((ctx, services, logConfig) =>
                {
                    var loggingSettings = ctx.Configuration
                        .GetSection($"{MonitoringConfig.SectionName}:Logging")
                        .Get<LoggingConfig>() ?? new LoggingConfig();

                    var minLevel = loggingSettings.MinimumLevel switch
                    {
                        "Debug"   => LogEventLevel.Debug,
                        "Warning" => LogEventLevel.Warning,
                        "Error"   => LogEventLevel.Error,
                        _         => LogEventLevel.Information
                    };

                    logConfig
                        .MinimumLevel.Is(minLevel)
                        .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                        .MinimumLevel.Override("System", LogEventLevel.Warning)
                        .Enrich.FromLogContext()
                        .Enrich.WithEnvironmentName()
                        .Enrich.WithMachineName()
                        .WriteTo.Console(outputTemplate:
                            "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
                        .WriteTo.File(
                            path: loggingSettings.FilePath,
                            rollingInterval: RollingInterval.Day,
                            retainedFileCountLimit: loggingSettings.RetainedFileCountLimit,
                            outputTemplate:
                                "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}");
                })
                // ── Windows Service support (runs as a Windows Service or console) ──
                .UseWindowsService(opts => opts.ServiceName = "MonitoringAgent")
                .ConfigureServices((ctx, services) =>
                {
                    // ── Configuration ──
                    services.Configure<MonitoringConfig>(
                        ctx.Configuration.GetSection(MonitoringConfig.SectionName));

                    // ── Infrastructure: SQL services (singleton — share connection string) ──
                    services.AddSingleton<IDatabaseProviderFactory, DatabaseProviderFactory>();
                    services.AddSingleton<IMetricCollector, SqlMetricCollector>();
                    services.AddSingleton<IQueryAnalyzer, QueryAnalyzerService>();
                    services.AddSingleton<IIndexAnalyzer, IndexAnalysisService>();
                    services.AddSingleton<IEventListener, EventListenerService>();

                    // ── Infrastructure: HTTP API sender ──
                    services.AddHttpClient<IApiSender, ApiSender>();

                    // ── Infrastructure: DiskSpd ──
                    services.AddSingleton<IDiskTestQueue, DiskTestQueue>();
                    services.AddSingleton<IDiskSpdService, DiskSpdService>();

                    // ── Application: Managers ──
                    services.AddSingleton<MetricService>();
                    services.AddSingleton<QueryAnalysisManager>();
                    services.AddSingleton<IndexAnalysisManager>();
                    services.AddSingleton<EventMonitorManager>();
                    services.AddSingleton<AlertManager>();
                    services.AddSingleton<RecommendationService>();
                    services.AddSingleton<ThresholdEvaluationService>();

                    // ── Worker ──
                    services.AddHostedService<Worker>();
                });
    }
}
