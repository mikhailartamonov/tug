# TugDSC — .NET 8 fork

[![build](https://github.com/mikhailartamonov/tug/actions/workflows/build.yml/badge.svg?branch=net8-port)](https://github.com/mikhailartamonov/tug/actions/workflows/build.yml)

A maintained-for-.NET-8 fork of [PowerShellOrg/tug](https://github.com/PowerShellOrg/tug),
a cross-platform **Desired State Configuration (DSC) pull server**. Upstream
targets `netcoreapp2.0` and has been dormant since 2019; this fork ports the
server to **.NET 8** so it builds and runs on a current LTS runtime, and adds
the tooling in this `contrib/` directory to keep the port reproducible and
verifiable.

> **Scope of the port.** The server and its two abstraction projects are ported
> (`TugDSC.Server.WebAppHost`, `TugDSC.Server.Abstractions`, `TugDSC.Abstractions`).
> The repo still contains unported PS5/client/FaaS projects — build the server
> `.csproj` directly, not the whole solution.

## What a DSC pull server does

Windows PowerShell nodes run a Local Configuration Manager (LCM). In **pull**
mode, each node checks in on its own schedule to:

1. **Register** with a shared registration key.
2. **Pull** the configuration (a `.mof`) assigned to it by name, plus any
   resource modules it needs.
3. **Apply** it — converging the machine to the desired state.
4. **Report** the result back to the server.

The server itself runs no PowerShell; it stores and serves MOFs + modules and
collects reports. Drift is corrected automatically between runs.

### Two node generations, one server

This fork serves **both** DSC pull protocols from the same endpoint, so modern
and legacy Windows nodes can share one server (per [MS-DSCPM]):

| | Protocol | Nodes | Addressed by | Auth |
|---|---|---|---|---|
| **v2** | `Nodes(AgentId=…)` | WMF 5.x (PowerShell 5.1) | `ConfigurationNames` | registration key + HMAC |
| **v1** | `Action(ConfigurationId=…)` | WMF 4.0 (PowerShell 4.0) | a `ConfigurationId` GUID | none (ConfigurationId is the token) |

A v1 node fetches `<ConfigurationId>.mof`; a v2 node registers, then fetches
`<ConfigurationName>.mof`. Both were verified end-to-end against real Windows
nodes. See [`docs/DEPLOY.md`](docs/DEPLOY.md) for onboarding each.

## Start here

New to this? Follow the path that matches what you're trying to do. Each step
links to the detail; you don't need to read everything up front.

**A. I just want the server running.**
1. Build & publish it → [Build & run](#build--run).
2. Put it behind TLS with a service unit → [`docs/DEPLOY.md`](docs/DEPLOY.md).
3. Confirm it's alive: open `/` (status page) or `GET /version`.

**B. I want to push a configuration to my Windows machines.** This is the common
case. End to end it's four moves — author, publish, onboard, watch:
1. **Author + compile** a config on a Windows box (PowerShell 5.1):
   ```powershell
   .\contrib\tools\New-TugConfig.ps1 -Name SystemReport -Template report -Compile
   ```
   This writes `SystemReport.ps1` (edit it however you like) and compiles
   `SystemReport.mof`. Templates: `report`, `file`, `service`, `feature`.
2. **Publish** the MOF on the server (handles both store paths + checksum):
   ```bash
   contrib/tools/tug-publish SystemReport.mof SystemReport
   ```
3. **Onboard** the node — point its LCM at the server and name the config →
   [Onboard a Windows node](#onboard-a-windows-node).
4. **Watch** it converge: on the node, `Update-DscConfiguration -Wait -Verbose`,
   then check `Reports/` on the server.

   Want the *why* behind each step (resource types, `Script` blocks, pitfalls)?
   Read [`docs/MOF-AUTHORING.md`](docs/MOF-AUTHORING.md).

**C. I want to prove the server actually works** (no Windows client needed) →
[Verify a server](#verify-a-server-without-a-windows-client). One command, 17
checks.

**D. I'm maintaining the port itself** (build, CI, what changed from upstream) →
[`docs/NET8-PORT.md`](docs/NET8-PORT.md) and [CI](#continuous-integration).

## Contents of `contrib/`

```
contrib/
├── tools/
│   ├── New-TugConfig.ps1    # (Windows) scaffold a config from a template + compile to MOF
│   ├── tug-publish          # (server)  deploy a MOF to both paths + write checksum + verify
│   ├── dsc_server_test.py   # full pull-protocol test harness (HMAC, pure Python)
│   └── winrm_helper.py      # compile MOFs on a Windows node over WinRM
├── configs/
│   ├── SystemReport.ps1     # sample DSC config: host-inventory report
│   └── SystemReport.mof     # its compiled MOF (the harness's reference artifact)
└── docs/
    ├── NET8-PORT.md         # what the port changed and why
    ├── MOF-AUTHORING.md     # how to write, compile & publish MOFs  ← read this
    └── DEPLOY.md            # run behind nginx with a systemd unit
```

## Architecture

```
Windows node (LCM, pull mode)
        │  HTTPS, HMAC-signed, MOF over the DSC v2 protocol
        ▼
   reverse proxy (nginx, TLS termination)      ── see docs/DEPLOY.md
        │  HTTP on loopback
        ▼
   TugDSC server (.NET 8 / Kestrel, 127.0.0.1:5000)
        │
        ▼
   file store  /var/lib/tug/*   (configs, modules, registrations, reports)
```

## Build & run

Requires the .NET 8 SDK to build; a framework-dependent publish runs against the
`aspnetcore-runtime-8.0` package.

```bash
git clone https://github.com/mikhailartamonov/tug
cd tug && git checkout net8-port
dotnet publish src/TugDSC.Server.WebAppHost/TugDSC.Server.WebAppHost.csproj \
    -c Release -o ./publish
ASPNETCORE_URLS=http://127.0.0.1:5000 ./publish/TugDSC.Server.WebAppHost
```

Health check: `GET /version` → `{"version":"0.7.0.0"}`. The root page (`/`) is a
Windows/Fluent-styled status page describing the server.

For a production layout (systemd unit, nginx front, TLS), see
[`docs/DEPLOY.md`](docs/DEPLOY.md).

### File store layout (`/var/lib/tug`)

| Path                     | Holds                                             |
|--------------------------|---------------------------------------------------|
| `Authz/RegistrationKeys.txt` | valid registration keys, one GUID per line    |
| `Registrations/`         | `<agentId>.json` written when a node registers    |
| `Configuration/`         | `<Name>.mof` (+ `<Name>.mof.checksum`)            |
| `Configuration/SHARED/`  | a copy of each `<Name>.mof` (see note below)       |
| `Modules/`               | `<Name>_<Version>.zip` (+ `.checksum`)            |
| `Reports/`               | `<agentId>/<jobId>.json` node status reports       |

> **Deploy configs to both `Configuration/` and `Configuration/SHARED/`.**
> `GetDscAction` consults `SHARED/` to decide if a node needs an update, while
> `GetConfiguration` serves bytes from `Configuration/`. This split is an
> upstream quirk carried into the fork.

## Author & publish a configuration

Two helper tools cover the whole path; the full recipe (resource types, `Script`
blocks, encoding, pitfalls) is in [`docs/MOF-AUTHORING.md`](docs/MOF-AUTHORING.md).

**On Windows — `New-TugConfig.ps1`** scaffolds a starter config and compiles it,
cleaning the build folder first so you never ship a stale `localhost.mof`:

```powershell
.\contrib\tools\New-TugConfig.ps1 -Name SystemReport -Template report -Compile
# writes SystemReport.ps1 (edit freely) and compiles SystemReport.mof
```

**On the server — `tug-publish`** deploys the MOF to both store paths and writes
the uppercase SHA-256 checksum (the two things most often gotten wrong by hand):

```bash
contrib/tools/tug-publish SystemReport.mof SystemReport
# add --verify to also run the protocol harness against the running server
```

Prefer to do it by hand? The equivalent raw commands and the reasoning are in
[`docs/MOF-AUTHORING.md`](docs/MOF-AUTHORING.md).

## Onboard a Windows node

Set the LCM to pull from the server and name the configuration to pull. Full
meta-config in [`docs/DEPLOY.md`](docs/DEPLOY.md); the essentials:

```powershell
[DSCLocalConfigurationManager()]
configuration NodeLCM {
    Node localhost {
        Settings { RefreshMode = 'Pull'; ConfigurationMode = 'ApplyAndAutoCorrect' }
        ConfigurationRepositoryWeb Tug {
            ServerURL          = 'https://your-server'
            RegistrationKey    = '<registration-key-guid>'
            ConfigurationNames = @('SystemReport')
        }
        ReportServerWeb TugReport {
            ServerURL = 'https://your-server'; RegistrationKey = '<registration-key-guid>'
        }
    }
}
NodeLCM -OutputPath C:\DSC\LCM
Set-DscLocalConfigurationManager -Path C:\DSC\LCM
Update-DscConfiguration -Wait -Verbose
```

## Verify a server without a Windows client

`tools/dsc_server_test.py` speaks the DSC v2 pull protocol directly, signing
each request with the registration-key HMAC. It exercises the happy path
byte-for-byte and the authorization edge cases — no LCM required.

```bash
pip install requests pywinrm
export DSC_SERVER_URL="https://your-server"
export DSC_REG_KEY="<a-registration-key-guid>"
python3 contrib/tools/dsc_server_test.py
```

Output ends with `17/17 checks passed`, covering:

- register → GetDscAction (empty checksum → `GetConfiguration`)
- GetConfiguration: HTTP 200, `Checksum` / `ChecksumAlgorithm` headers, body
  byte-identical to the deployed MOF
- GetDscAction with the correct checksum → `OK` (idempotent, no re-pull)
- SendReport → GetReports round-trip
- negatives: wrong registration key → 401, forged/absent signature on an
  unknown agent → 401, unknown configuration → 404

Point it at your own MOF with `DSC_MOF_PATH=/path/to/your.mof`.

## Troubleshooting

| Symptom | Cause & fix |
|---------|-------------|
| **HTTP 400** on register / GetDscAction | The server's `VeryStrictInputFilter` re-serializes the parsed body with Newtonsoft and compares it byte-for-byte to the raw request. The JSON must be **compact** (no spaces after `:` / `,`) and in exact model field order. Real LCM clients do this; hand-built requests must too. |
| **"Synchronous operations are disallowed"** on register | The authz filters read the body synchronously. The port enables Kestrel `AllowSynchronousIO`; if you rebuild, keep that. |
| Node reports **up to date but never pulls** | The config is missing from `Configuration/SHARED/`. Deploy to both paths. |
| Node **rejects the download** / checksum error | `<Name>.mof.checksum` missing, wrong, or lowercase. It must be the **uppercase** SHA-256 of the deployed bytes. |
| Shipped the **wrong MOF** | A stale `localhost.mof` was left in the output folder. Clean it before compiling and verify the content (`Select-String 'instance of MSFT_'`). |
| LCM crashes with **"Message Index (zero based)…"** before any network call | A client-side bug in some WMF 5.1 builds (`WebDownloadManager`), not the server. Patch Windows/WMF on that node. The Python harness bypasses it entirely. |

More background on the port's two runtime fixes is in
[`docs/NET8-PORT.md`](docs/NET8-PORT.md).

## Continuous integration

`.github/workflows/build.yml` runs on every push/PR to `net8-port`:

- **build** — restore / build / `dotnet publish` the server on .NET 8 and upload
  the framework-dependent output as an artifact.
- **tooling** — `py_compile` the Python harness so it can't rot silently.

## License

Inherits upstream Tug's MIT license (see `LICENSE`).
