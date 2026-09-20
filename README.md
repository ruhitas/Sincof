# Sincof

Public companion source code for the related IEEE conference paper.

This repository contains **two** projects:

| Folder | Description |
|--------|-------------|
| [`MonitoringAgent/`](MonitoringAgent/) | Multi-provider database monitoring agent + ASP.NET Core dashboard (SQL Server, Azure SQL, PostgreSQL, MySQL, Oracle) |
| [`WorkerServiceSQL/`](WorkerServiceSQL/) | .NET Worker Service starter / companion worker host |

> No personal author credentials, private connection strings, or local machine paths are included.

---

## Quick start — MonitoringAgent

```powershell
cd MonitoringAgent
dotnet restore MonitoringAgent.sln
dotnet build MonitoringAgent.sln -c Release
dotnet run --project MonitoringAgent.Web --launch-profile http
dotnet run --project MonitoringAgent.Worker
```

Dashboard: [http://localhost:5230](http://localhost:5230)

Full setup, configuration, and security notes: [`MonitoringAgent/README.md`](MonitoringAgent/README.md)

---

## Quick start — WorkerServiceSQL

```powershell
cd WorkerServiceSQL
dotnet restore WorkerServiceSQL.sln
dotnet build WorkerServiceSQL.sln -c Release
dotnet run --project WorkerServiceSQL
```

---

## Requirements

- Windows 10/11 or Windows Server
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- SQL Server LocalDB or SQL Server (for MonitoringAgent dashboard)

---

## Repository layout

```
Sincof/
├── README.md
├── MonitoringAgent/
│   ├── MonitoringAgent.sln
│   ├── MonitoringAgent.Core/
│   ├── MonitoringAgent.Infrastructure/
│   ├── MonitoringAgent.Application/
│   ├── MonitoringAgent.Worker/
│   └── MonitoringAgent.Web/
└── WorkerServiceSQL/
    ├── WorkerServiceSQL.sln
    └── WorkerServiceSQL/
```

---

## License / citation

Research and educational use accompanying the related conference paper.  
Third-party components (for example DiskSpd under `MonitoringAgent`) remain under their own licenses.

```bibtex
@inproceedings{YourPaperKey2026,
  title   = {Your Paper Title},
  author  = {Author One and Author Two},
  booktitle = {Proceedings of the Conference},
  year    = {2026}
}
```
