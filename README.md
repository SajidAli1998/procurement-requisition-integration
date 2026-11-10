# Procurement Requisition Integration

A .NET 10 service that moves approved purchase requisitions from an internal system into SAP and keeps their status in sync, without losing any and without creating any twice. Everything runs locally against two mock systems (the internal source and an SAP-style service) loaded with the client's workbook in `data/`. No real SAP system is involved.

The repository also contains a Test Lab: a web app where you can run the sync yourself and break it on purpose (pause it, cut the connection, crash the server, send duplicates) and see straight away whether anything went wrong.

## What you need

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Node.js 20 or later](https://nodejs.org) for the Test Lab web app
- On Windows, [PowerShell 7](https://learn.microsoft.com/powershell/scripting/install/installing-powershell-on-windows) (`pwsh`) for the scripts

## Try it in the browser

macOS or Linux:

```bash
scripts/testlab.sh
```

Windows:

```powershell
pwsh scripts/testlab.ps1
```

The first start installs the web app's packages, which takes a minute. Then open http://localhost:3000.

Start with **Try it yourself**:

1. **Load the client's data**: 120 requisitions, 7 of them already in SAP.
2. **Start the sync.** Slow motion gives you time to step in while it runs.
3. **Cause trouble** at any point: pause and resume, switch the connection to SAP or to the source off, make it unreliable, crash the server, or send a requisition twice.
4. **Did it work?** The panel next to the steps answers that at every moment. It shows how many requisitions are in SAP and how many are still to send, plus three checks against SAP's own records:
   - no requisition exists twice;
   - none was lost;
   - nothing is marked done that SAP doesn't have.

   If something is unfinished, it tells you what to do next, for example "turn the connection back on, then run the sync again".

**What if** has the same situations as one-click demos, each explained in plain words afterwards. The dashboard, requisition list and reconciliation report show the details. The pages under **Advanced** are for deeper testing: the full scenario catalogue, detailed fault injection, the AI assistant API and data import.

Stop everything with `scripts/testlab-stop.sh` (Windows: `pwsh scripts/testlab-stop.ps1`).

## Run the tests

```bash
dotnet test
```

This runs the unit tests, the scenario tests (the real sync code against both mocks, with faults injected) and the Test Lab API tests. It takes about three minutes.

The web app has its own tests:

```bash
cd web
npm test
npx playwright test
```

The Playwright tests need the Test Lab running (`scripts/testlab.sh`). The first time, install the browser with `npx playwright install chromium`.

## Run the scenario catalogue in the terminal

There are 374 named situations: lost replies, crashes, duplicates, races, bad data, security abuse and more. Each one runs in its own isolated copy of the system and is checked against what SAP actually holds.

```bash
scripts/scenarios.sh list
scripts/scenarios.sh run DUP-012
scripts/scenarios.sh run --all
```

`run` also accepts prefixes (`'SAP-*'`), `--category`, `--mode` (for example `Recovery` or `Security`), `--priority` and `--search`. Results are saved under `reports/scenarios/`. A scenario that can't run in this setup is reported as Blocked with the reason, never as passed. On Windows use `pwsh scripts/scenarios.ps1` with the same arguments.

## Run the integration on its own

```bash
scripts/demo.sh
```

On Windows: `pwsh scripts/demo.ps1`.

This guided demo starts the source mock (:5101), the SAP mock (:5102) and the assistant API (:5103), then walks through a full sync of the workbook. It covers retries, quarantine, a rerun that creates nothing, status updates from SAP, and the AI assistant API. Press Enter between steps, or pass `--no-pause`.

To drive it by hand, start the services with `scripts/run-all.sh` (stop them with `scripts/stop-all.sh`) and use the `procsync` command line:

```bash
dotnet run --project src/Procurement.Sync.Worker -- run
```

| Command | What it does |
|---|---|
| `run` | One sync cycle. Exit code 0 means clean, 1 means some requisitions are still waiting for a retry |
| `serve --interval 60` | Keeps syncing every 60 seconds |
| `status <id>` | Where one requisition stands, with its history |
| `quarantine list` / `show <id>` / `replay <id>` / `resolve <id> --note "..."` | Look at and act on requisitions held back for bad data |
| `reconcile` | Checks SAP against the integration's records and writes a report |
| `check-expected` | Compares the last run with the workbook's expected results |

Each run writes a report to `reports/` (open `reports/latest.html`). Every service has interactive API docs at `/scalar`, for example http://localhost:5103/scalar. The OpenAPI files are in `api/`.

## Configuration

The integration's settings are in `src/Procurement.Sync.Worker/appsettings.json`. Any of them can be overridden with an environment variable, for example `Sync__Parallelism=8`. It uses SQLite by default; set `Store__Provider=SqlServer` and `ConnectionStrings__SyncStore` to use SQL Server.

The Test Lab API refuses changes that don't carry its key. The scripts generate one and share it with the web app. To choose your own, set `TESTLAB_KEY` (at least 16 characters) before starting.

## Docker

If you have Docker:

```bash
TESTLAB_KEY=$(openssl rand -hex 16) docker compose up --build
```

This starts the Test Lab API and the web app on the same ports as above.

To host it as a single service, the root `Dockerfile` runs both in one container and serves the web app on `$PORT`. `render.yaml` deploys that image to Render's free plan.

## Layout

| Folder | Contents |
|---|---|
| `src/Procurement.Sync` | The integration: clients, retry rules, pipeline, state store, reconciliation |
| `src/Procurement.Sync.Worker` | The `procsync` command line and scheduled worker |
| `src/Procurement.AssistantApi` | API for an AI assistant: drafts, validation, status |
| `src/Mocks.*` | The source and SAP mocks, with fault injection |
| `src/Procurement.Scenarios` | The scenario engine and catalogue |
| `src/Procurement.TestLab` | The `testlab` command line and the API behind the web app |
| `web` | The Test Lab web app (Next.js) |
| `tests` | .NET tests |
| `data` | The client's workbook and the employee directory |
| `api` | OpenAPI documents and MCP tool definitions |
| `scripts` | Start, stop, demo and scenario scripts |
