using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MonitoringAgent.Core.Configuration;
using MonitoringAgent.Core.Interfaces;
using MonitoringAgent.Infrastructure.Providers;
using MonitoringAgent.Infrastructure.Sql;

namespace MonitoringAgent.Infrastructure
{
    public interface IDatabaseProviderFactory
    {
        IMetricCollector GetCollector(DatabaseTarget target);
        IQueryAnalyzer GetQueryAnalyzer(DatabaseTarget target);
        IIndexAnalyzer GetIndexAnalyzer(DatabaseTarget target);
    }

    public class DatabaseProviderFactory : IDatabaseProviderFactory
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly IOptions<MonitoringConfig> _options;
        private readonly ILoggerFactory _loggerFactory;

        public DatabaseProviderFactory(
            IServiceProvider serviceProvider, 
            IOptions<MonitoringConfig> options,
            ILoggerFactory loggerFactory)
        {
            _serviceProvider = serviceProvider;
            _options = options;
            _loggerFactory = loggerFactory;
        }

        public IMetricCollector GetCollector(DatabaseTarget target)
        {
            var logger = _loggerFactory.CreateLogger(target.Alias);

            return target.ProviderType.ToLower() switch
            {
                "sqlserver" => new SqlMetricCollector(_options, _loggerFactory.CreateLogger<SqlMetricCollector>(), target.ConnectionString),
                "postgres"  => new PostgresMetricCollector(target.ConnectionString, logger),
                "mysql"     => new MySqlMetricCollector(target.ConnectionString, logger),
                "oracle"    => new OracleMetricCollector(target.ConnectionString, logger),
                "azuresql"  => new AzureSqlMetricCollector(target.ConnectionString, _options, _loggerFactory.CreateLogger<SqlMetricCollector>()),
                _           => throw new NotSupportedException($"Provider {target.ProviderType} is not supported.")
            };
        }

        public IQueryAnalyzer GetQueryAnalyzer(DatabaseTarget target)
        {
            var logger = _loggerFactory.CreateLogger($"{target.Alias}_Query");

            return target.ProviderType.ToLower() switch
            {
                "sqlserver" or "azuresql" => new QueryAnalyzerService(_options, _loggerFactory.CreateLogger<QueryAnalyzerService>()),
                "mysql"     => new MySqlAnalyzerService(target.ConnectionString, logger),
                "postgres"  => new PostgresAnalyzerService(target.ConnectionString, logger),
                "oracle"    => new OracleAnalyzerService(target.ConnectionString, logger),
                _           => throw new NotSupportedException($"Query analysis for {target.ProviderType} is not supported.")
            };
        }

        public IIndexAnalyzer GetIndexAnalyzer(DatabaseTarget target)
        {
            var logger = _loggerFactory.CreateLogger($"{target.Alias}_Index");

            return target.ProviderType.ToLower() switch
            {
                "sqlserver" or "azuresql" => new IndexAnalysisService(_options, _loggerFactory.CreateLogger<IndexAnalysisService>()),
                "mysql"     => new MySqlAnalyzerService(target.ConnectionString, logger), // Same service handles both
                "postgres"  => new PostgresAnalyzerService(target.ConnectionString, logger),
                "oracle"    => new OracleAnalyzerService(target.ConnectionString, logger),
                _           => throw new NotSupportedException($"Index analysis for {target.ProviderType} is not supported.")
            };
        }
    }
}
