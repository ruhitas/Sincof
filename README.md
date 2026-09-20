# MonitoringAgent

Multi-provider database monitoring agent and web dashboard for SQL Server, Azure SQL, PostgreSQL, MySQL, and Oracle.

The solution collects instance and database metrics, detects long-running / high-cost queries, blocking, deadlocks, and index issues, generates recommendations and alerts, and optionally runs DiskSpd storage benchmarks on Windows.

> Companion source code for the related IEEE conference paper. This repository intentionally contains **no personal author credentials, private connection strings, or local machine paths**.

---

## Architecture

| Project | Role |
|---------|------|
| `MonitoringAgent.Core` | Models, DTOs, interfaces, configuration types |
| `MonitoringAgent.Infrastructure` | Collectors, analyzers, API sender, DiskSpd helper |
| `MonitoringAgent.Application` | Monitoring orchestration and recommendations |
| `MonitoringAgent.Worker` | Background collector (console / Windows Service) |
| `MonitoringAgent.Web` | ASP.NET Core MVC dashboard and persistence API |

```
Target DBs  -->  Worker (collect / analyze)  -->  Web API + Dashboard (LocalDB / SQL Server)
```

---

## Requirements

- Windows 10/11 or Windows Server (Worker uses Windows Service hosting; DiskSpd is Windows-only)
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- SQL Server LocalDB **or** SQL Server / Azure SQL (for the dashboard database)
- Optional targets: PostgreSQL, MySQL, Oracle (enable the matching feature toggles)
- Optional: [DiskSpd](https://github.com/microsoft/diskspd) (bundled under `MonitoringAgent.Worker/DiskSpd` ÔÇö see Microsoft EULA in that folder)

---

## Quick start

### 1. Clone and restore

```powershell
git clone https://github.com/YOUR_ORG/MonitoringAgent.git
cd MonitoringAgent
dotnet restore MonitoringAgent.sln
dotnet build MonitoringAgent.sln -c Release
```

### 2. Configure the Web dashboard database

Edit `MonitoringAgent.Web/appsettings.json`:

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Server=(localdb)\\MSSQLLocalDB;Database=MonitoringAgentDb;Trusted_Connection=True;MultipleActiveResultSets=true;TrustServerCertificate=True"
  }
}
```

On first run the Web app creates/updates the schema automatically (`EnsureCreated` + additive SQL patches).

### 3. Configure the Worker

Edit `MonitoringAgent.Worker/appsettings.json`:

1. Set `SqlMonitoring:ConnectionString` to a server you are allowed to monitor (Integrated Security recommended for local demos).
2. Set `IncludeDBs` to the databases you want to monitor (or leave empty and use `TargetServers`).
3. Set `Api:BaseUrl` to the Web URL (default `http://localhost:5230`).
4. Set `Api:ApiKey` to a shared secret (must match the value configured in the Web UI / worker config). Replace `CHANGE_ME_API_KEY`.
5. Keep `EnableApiSending` / provider toggles as needed.

Provider samples (disabled by default):

- `appsettings.SqlServer.json`
- `appsettings.Postgres.json`
- `appsettings.MySql.json`
- `appsettings.Oracle.json`
- `appsettings.AzureSql.json`

Copy values into `appsettings.json` / `TargetServers`, or layer them with the ASP.NET Core environment configuration pattern. **Never commit real passwords.**

Example target server entry:

```json
{
  "Alias": "Local_SqlServer",
  "ProviderType": "SqlServer",
  "ConnectionString": "Server=(localdb)\\MSSQLLocalDB;Database=YourDatabase;Integrated Security=True;TrustServerCertificate=True;",
  "IsEnabled": true
}
```

### 4. Run the Web dashboard

```powershell
dotnet run --project MonitoringAgent.Web --launch-profile http
```

Open: [http://localhost:5230](http://localhost:5230)

### 5. Run the Worker

```powershell
dotnet run --project MonitoringAgent.Worker
```

With API sending enabled, the Worker posts payloads to the Web app. You can also use **Offline Import** in the UI to load saved JSON payloads from a folder.

---

## Configuration reference (`SqlMonitoring`)

| Section | Purpose |
|---------|---------|
| `ConnectionString` / `TargetServers` | Monitored instance(s) |
| `IncludeDBs` / `ExcludeDBs` | Database filters |
| `CollectionIntervals` | Metrics / queries / indexes / events cadence |
| `FeatureToggles` | Enable/disable collectors and providers |
| `AlertThresholds` / `Thresholds` | Alert and recommendation thresholds |
| `IndexAnalysis` | Fragmentation / unused / missing index rules |
| `Api` | Dashboard base URL, API key, retries |
| `AdvancedControls` | Circuit breaker, throttle, maintenance windows |
| `Logging` | Serilog file path and retention |
| `DiskSpd` | Storage benchmark path and parameters (`EnableDiskSpd`) |

### Feature toggles (common)

- `EnableMetrics`, `EnableQueries`, `EnableIndexAnalysis`
- `EnableBlockingDetection`, `EnableDeadlockDetection`, `EnableAlerts`
- `EnableApiSending` ÔÇö send results to the Web API
- `EnablePostgres`, `EnableMySql`, `EnableOracle`, `EnableAzureSql`
- `EnableDiskSpd` ÔÇö Windows DiskSpd benchmark (off by default in the public sample)

---

## Web UI overview

- **Dashboard** ÔÇö recent metrics and health
- **Servers / Workers** ÔÇö manage worker configs and target servers
- **Queries / Indexes / Alerts / Blocking / Deadlocks** ÔÇö analysis views
- **Offline Import** ÔÇö import `payload_YYYYMMDD_HHMMSS.json` files from a folder

Default API key placeholder used in templates: `CHANGE_ME_API_KEY` (change it before enabling `EnableApiSending`).

---

## Install as a Windows Service (optional)

Publish and register (run PowerShell as Administrator):

```powershell
dotnet publish MonitoringAgent.Worker -c Release -o C:\Services\MonitoringAgent
sc.exe create MonitoringAgent binPath= "C:\Services\MonitoringAgent\MonitoringAgent.Worker.exe" start= auto
sc.exe start MonitoringAgent
```

Update `appsettings.json` under the publish folder before starting the service. Logs write to `Logs/` relative to the working directory.

---

## Permissions (monitored SQL Server)

Grant the monitoring login only what you need, for example:

- `VIEW SERVER STATE`
- `VIEW ANY DEFINITION` (as required by your collectors)
- Database-level read rights on included databases

Prefer a dedicated low-privilege login over `sa`.

---

## Security notes for public use

1. Replace every `YOUR_PASSWORD` / `CHANGE_ME_API_KEY` before running against real systems.
2. Do not commit `appsettings.*.local.json` or production connection strings (see `.gitignore`).
3. Rotate any credentials that were ever committed in private history before opening the repository publicly.
4. DiskSpd is third-party Microsoft software; redistribution is subject to the EULA in `MonitoringAgent.Worker/DiskSpd`.

---

## Solution build / test commands

```powershell
dotnet restore MonitoringAgent.sln
dotnet build MonitoringAgent.sln -c Release
dotnet run --project MonitoringAgent.Web --launch-profile http
dotnet run --project MonitoringAgent.Worker
```

---

## Project layout

```
MonitoringAgent/
Ôö£ÔöÇÔöÇ MonitoringAgent.sln
Ôö£ÔöÇÔöÇ MonitoringAgent.Core/
Ôö£ÔöÇÔöÇ MonitoringAgent.Infrastructure/
Ôö£ÔöÇÔöÇ MonitoringAgent.Application/
Ôö£ÔöÇÔöÇ MonitoringAgent.Worker/
Ôöé   Ôö£ÔöÇÔöÇ appsettings.json
Ôöé   Ôö£ÔöÇÔöÇ appsettings.*.json          # provider samples
Ôöé   ÔööÔöÇÔöÇ DiskSpd/                    # optional DiskSpd binaries + EULA
ÔööÔöÇÔöÇ MonitoringAgent.Web/
    Ôö£ÔöÇÔöÇ Controllers/
    Ôö£ÔöÇÔöÇ Views/
    Ôö£ÔöÇÔöÇ Data/
    ÔööÔöÇÔöÇ appsettings.json
```

---

## License

Source code in this repository is provided for research and educational use accompanying the related conference paper.  
Third-party components (including DiskSpd) remain under their own licenses.

If you republish results, please cite the corresponding IEEE paper (authors and title as published in the proceedings).

---

## Citation

```bibtex
@inproceedings{YourPaperKey2026,
  title   = {Your Paper Title},
  author  = {Author One and Author Two},
  booktitle = {Proceedings of the Conference},
  year    = {2026}
}
```

Update the BibTeX entry with the final camera-ready title, author list, and venue before public release.
