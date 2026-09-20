using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MonitoringAgent.Core.Configuration;
using MonitoringAgent.Core.Interfaces;
using MonitoringAgent.Core.Models;

namespace MonitoringAgent.Infrastructure.Disk
{
    public class DiskSpdService : IDiskSpdService
    {
        private readonly ILogger<DiskSpdService> _logger;
        private readonly MonitoringConfig _config;
        private readonly string _basePath;

        public DiskSpdService(ILogger<DiskSpdService> logger, IOptions<MonitoringConfig> options)
        {
            _logger = logger;
            _config = options.Value;
            _basePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DiskSpd");
        }

        public async Task<DiskTestResult> RunAsync(DiskTestRequest request, CancellationToken ct = default)
        {
            var result = new DiskTestResult
            {
                RequestId = request.RequestId,
                ServerName = request.ServerName,
                TestPath = request.Path,
                FileSizeGB = request.FileSizeGB,
                DurationSec = request.DurationSec,
                WritePercent = request.WritePercent,
                BlockSizeKB = request.BlockSizeKB,
                Threads = request.Threads,
                QueueDepth = request.QueueDepth,
                Success = false
            };

            try
            {
                string exePath = GetDiskSpdPath();
                if (!File.Exists(exePath))
                {
                    result.ErrorMessage = $"DiskSpd binary not found at: {exePath}";
                    _logger.LogError(result.ErrorMessage);
                    return result;
                }

                string args = $"-c{request.FileSizeGB}G -d{request.DurationSec} -r -w{request.WritePercent} -t{request.Threads} -o{request.QueueDepth} -b{request.BlockSizeKB}K -Sh -L \"{request.Path}\"";

                _logger.LogInformation("Running DiskSpd: {Exe} {Args}", exePath, args);

                var startInfo = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = args,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = new Process { StartInfo = startInfo };
                process.Start();

                var outputTask = process.StandardOutput.ReadToEndAsync();
                var errorTask = process.StandardError.ReadToEndAsync();

                var timeoutTask = Task.Delay(TimeSpan.FromSeconds(request.DurationSec + 30), ct);
                var completedTask = await Task.WhenAny(process.WaitForExitAsync(ct), timeoutTask);

                if (completedTask == timeoutTask)
                {
                    process.Kill();
                    result.ErrorMessage = "DiskSpd execution timed out.";
                    _logger.LogError(result.ErrorMessage);
                    return result;
                }

                string output = await outputTask;
                string error = await errorTask;

                result.RawOutput = output;

                if (process.ExitCode != 0)
                {
                    result.ErrorMessage = $"DiskSpd exited with code {process.ExitCode}. Error: {error}";
                    _logger.LogError(result.ErrorMessage);
                    return result;
                }

                ParseResults(output, result);
                result.Success = true;
                result.CompletedAt = DateTime.UtcNow;

                _logger.LogInformation("DiskSpd completed for request {Id}: IOPS={IOPS}, MB/s={MBps}, Latency={Latency}ms, CPU={CPU}%", 
                    request.RequestId, result.IOPS, result.MBps, result.LatencyMs, result.CpuUsagePercent);
            }
            catch (Exception ex)
            {
                result.ErrorMessage = $"DiskSpd execution failed: {ex.Message}";
                _logger.LogError(ex, "DiskSpd execution failed for request {Id}", request.RequestId);
            }

            return result;
        }

        private string GetDiskSpdPath()
        {
            string architecture = _config.DiskSpd.PreferredArchitecture;

            if (string.IsNullOrWhiteSpace(architecture))
            {
                architecture = RuntimeInformation.ProcessArchitecture switch
                {
                    Architecture.X64 => "amd64",
                    Architecture.X86 => "x86",
                    Architecture.Arm64 => "arm64",
                    _ => throw new PlatformNotSupportedException($"Architecture {RuntimeInformation.ProcessArchitecture} not supported.")
                };
            }

            return Path.Combine(_basePath, architecture, "diskspd.exe");
        }

        private void ParseResults(string output, DiskTestResult result)
        {
            try
            {
                // 1. Parse IOPS, MBps, and Latency from the "total:" line
                // The table starts with "Total IO" and has a "total:" footer
                var totalIoRegex = new Regex(@"total:\s+\d+\s+\|\s+\d+\s+\|\s+(?<mbps>[\d\.]+)\s+\|\s+(?<iops>[\d\.]+)\s+\|\s+(?<avg>[\d\.]+)", RegexOptions.Compiled);
                var match = totalIoRegex.Match(output);
                if (match.Success)
                {
                    result.MBps = Double.Parse(match.Groups["mbps"].Value);
                    result.IOPS = Double.Parse(match.Groups["iops"].Value);
                    result.LatencyMs = Double.Parse(match.Groups["avg"].Value);
                }

                // 2. Parse CPU Usage
                // We look for the "CPU utilization" section specifically to avoid matching other "avg." lines
                // Standard block:
                // CPU utilization:
                //   processor |  %_total |    %_user |  %_kernel |    %_idle
                // ------------+----------+-----------+-----------+-----------
                //        avg. |    4.28% |     1.17% |     3.11% |    95.72%

                var cpuSectionMatch = Regex.Match(output, @"CPU utilization:.*?\n-+\n(.*?)(?:\n\r?\n|\z)", RegexOptions.Singleline);
                if (cpuSectionMatch.Success)
                {
                    var cpuMatch = Regex.Match(cpuSectionMatch.Groups[1].Value, @"avg\.\s*\|\s*(?<usage>[\d\.]+)%", RegexOptions.Compiled);
                    if (cpuMatch.Success)
                    {
                        double rawCpu = Double.Parse(cpuMatch.Groups["usage"].Value);
                        
                        // DiskSpd sometimes reports total aggregate CPU usage across all cores (sum).
                        // If the value is > 100%, we normalize it by core count to show % of total capacity.
                        if (rawCpu > 100.0)
                        {
                            int coreCount = Environment.ProcessorCount;
                            result.CpuUsagePercent = Math.Round(rawCpu / coreCount, 2);
                            _logger.LogDebug("Normalizing DiskSpd CPU: {Raw}% / {Cores} cores = {Normalized}%", rawCpu, coreCount, result.CpuUsagePercent);
                        }
                        else
                        {
                            result.CpuUsagePercent = rawCpu;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse DiskSpd output accurately. Raw output may be inconsistent.");
            }
        }
    }
}
