using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MonitoringAgent.Core.Configuration;
using MonitoringAgent.Core.Interfaces;
using MonitoringAgent.Core.Models;

namespace MonitoringAgent.Infrastructure.Api
{
    // ═══════════════════════════════════════════════════════════════
    // API SENDER — Posts JSON payloads to the central monitoring API
    //   - Configurable base URL, API key, timeout
    //   - Retry with exponential backoff
    //   - Never throws — all errors are logged and swallowed
    // ═══════════════════════════════════════════════════════════════
    public class ApiSender : IApiSender
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<ApiSender> _logger;
        private readonly ApiConfig _apiConfig;
        private readonly bool _enabled;

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };

        public ApiSender(HttpClient httpClient, IOptions<MonitoringConfig> options, ILogger<ApiSender> logger)
        {
            _httpClient = httpClient;
            _logger = logger;
            _apiConfig = options.Value.Api;
            _enabled = options.Value.FeatureToggles.EnableApiSending;

            // Configure HttpClient from settings
            if (!string.IsNullOrWhiteSpace(_apiConfig.BaseUrl))
            {
                _httpClient.BaseAddress = new Uri(_apiConfig.BaseUrl.TrimEnd('/') + "/");
            }

            if (!string.IsNullOrWhiteSpace(_apiConfig.ApiKey))
            {
                _httpClient.DefaultRequestHeaders.Add("X-API-KEY", _apiConfig.ApiKey);
            }

            _httpClient.Timeout = TimeSpan.FromSeconds(
                _apiConfig.TimeoutSeconds > 0 ? _apiConfig.TimeoutSeconds : 30);

            _httpClient.DefaultRequestHeaders.Accept.Add(
                new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
        }

        private static int _payloadCounter = 0;
        private static string _currentPayloadFile = $"payload_{DateTime.Now:yyyyMMdd_HHmmss}.json";
        private static readonly object _fileLock = new();

        /// <summary>
        /// Sends the full monitoring payload (metrics + queries + indexes + events) in a single POST.
        /// </summary>
        public async Task<bool> SendPayloadAsync(MonitoringPayload payload, CancellationToken ct = default)
        {
            if (!_enabled || string.IsNullOrWhiteSpace(_apiConfig.BaseUrl))
            {
                _logger.LogInformation("API sending disabled. Saving payload locally for testing.");
                SavePayloadLocally(payload);
                return true;
            }

            string provider = payload.InstanceMetrics?.ProviderType?.ToLower() ?? "sqlserver";
            return await PostWithRetryAsync($"api/monitoring/payload/{provider}", payload, ct);
        }

        private void SavePayloadLocally(MonitoringPayload payload)
        {
            try
            {
                string directory = "Logs/Payloads";
                if (!System.IO.Directory.Exists(directory))
                    System.IO.Directory.CreateDirectory(directory);

                lock (_fileLock)
                {
                    // Create a new file every 20 payloads to keep them readable
                    if (_payloadCounter >= 20)
                    {
                        _currentPayloadFile = $"payload_{DateTime.Now:yyyyMMdd_HHmmss}.json";
                        _payloadCounter = 0;
                    }

                    string filepath = System.IO.Path.Combine(directory, _currentPayloadFile);
                    var payloads = new List<MonitoringPayload>();

                    if (System.IO.File.Exists(filepath))
                    {
                        try
                        {
                            var existing = System.IO.File.ReadAllText(filepath);
                            payloads = JsonSerializer.Deserialize<List<MonitoringPayload>>(existing, _jsonOptions) ?? new();
                        }
                        catch { /* if file corrupted, start fresh */ }
                    }

                    payloads.Add(payload);

                    var writeOptions = new JsonSerializerOptions 
                    { 
                        WriteIndented = true, 
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase 
                    };
                    
                    System.IO.File.WriteAllText(filepath, JsonSerializer.Serialize(payloads, writeOptions));
                    _payloadCounter++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save payload locally.");
            }
        }

        /// <summary>
        /// Sends alert notifications separately for immediate processing.
        /// </summary>
        public async Task<bool> SendAlertsAsync(List<AlertDto> alerts, CancellationToken ct = default)
        {
            if (!_enabled || string.IsNullOrWhiteSpace(_apiConfig.BaseUrl))
            {
                if (alerts.Count > 0)
                    _logger.LogInformation("API sending disabled. Critical alerts generated but not sent: {Count}", alerts.Count);
                return true; 
            }

            if (alerts.Count == 0) return true;

            return await PostWithRetryAsync("api/monitoring/alerts", alerts, ct);
        }

        // ── Retry helper with exponential backoff ──
        private async Task<bool> PostWithRetryAsync<T>(string endpoint, T payload, CancellationToken ct)
        {
            int maxRetries = _apiConfig.RetryCount > 0 ? _apiConfig.RetryCount : 3;
            int delayMs = _apiConfig.RetryDelayMs > 0 ? _apiConfig.RetryDelayMs : 1000;

            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    var response = await _httpClient.PostAsJsonAsync(endpoint, payload, _jsonOptions, ct);

                    if (response.IsSuccessStatusCode)
                    {
                        _logger.LogDebug("Successfully sent data to {Endpoint} (attempt {Attempt})", endpoint, attempt);
                        return true;
                    }

                    var body = await response.Content.ReadAsStringAsync(ct);
                    _logger.LogWarning(
                        "API returned {StatusCode} for {Endpoint} (attempt {Attempt}/{Max}): {Body}",
                        (int)response.StatusCode, endpoint, attempt, maxRetries, body);
                }
                catch (TaskCanceledException) when (ct.IsCancellationRequested)
                {
                    _logger.LogInformation("API call to {Endpoint} cancelled", endpoint);
                    return false;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "API call to {Endpoint} failed (attempt {Attempt}/{Max})",
                        endpoint, attempt, maxRetries);
                }

                // Exponential backoff: 1s, 2s, 4s ...
                if (attempt < maxRetries)
                {
                    var backoff = delayMs * (int)Math.Pow(2, attempt - 1);
                    _logger.LogDebug("Retrying in {BackoffMs}ms...", backoff);
                    await Task.Delay(backoff, ct);
                }
            }

            _logger.LogError("All {MaxRetries} attempts to {Endpoint} failed", maxRetries, endpoint);
            return false;
        }
    }
}
