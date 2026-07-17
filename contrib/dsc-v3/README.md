# Self-hosted DSC v3 pull agent

DSC v3 (`dsc.exe`) is a standalone engine with **no pull server and no agent** —
Microsoft moved distribution out of DSC and expects an orchestrator (Intune,
Azure Arc/Machine Configuration, a CI runner, or Task Scheduler) to invoke it.

With **no Intune/Arc** (pure self-hosted), the orchestrator is **Task Scheduler +
this agent**. The agent is thin glue: it fetches a config from your server, then
hands it to a **stock engine** — `winget configure` or `dsc.exe config set`. It
invents no apply logic.

```
Task Scheduler ──► pull-agent.ps1 ──► download config from dsc.h-edu.online
                                  └──► winget configure  (or)  dsc config set
```

## Which engine on which OS

The two engines take **different config formats**, so pick the engine and pair it
with the matching config file.

| Engine | Config file | Where it runs | Notes |
|--------|-------------|---------------|-------|
| **`winget configure`** | `baseline.winget.yaml` | Windows 10/11, Server 2025 (winget in-box) | **Most fleets** — winget is present on client Windows. Brings its own DSC; nothing to bootstrap. |
| **`dsc.exe config set`** | `baseline.dsc.yaml` | Windows Server 2016/2019/2022 (+ Win10/11) | No winget needed; the agent bootstraps `dsc.exe` (a ~12 MB standalone binary, no LCM/WMI). |
| *(neither)* | — | Windows 2008 / 2012 R2, Win 7/8 | **Too old for dsc.exe.** Use the classic **v1 pull** (this repo's Tug server) for these. |

Both sample configs write a marker file (`C:\TugTest\hello-v3-*.txt`) via the
classic `PSDesiredStateConfiguration/File` resource, so success is easy to eyeball
— parity with the v1/v2 `hello.txt` test.

## Recipe A — winget (Win10/11, most fleets)

Host `baseline.winget.yaml` on your server, then on each node (elevated, once):

```powershell
.\Install-DscV3PullAgent.ps1 `
    -Engine winget `
    -ConfigUrl https://dsc.h-edu.online/v3/baseline.winget.yaml `
    -IntervalMinutes 30 -VerifyChecksum
```

## Recipe B — dsc.exe direct (Server 2016/2019/2022)

Host `baseline.dsc.yaml`, then on each node (elevated, once):

```powershell
.\Install-DscV3PullAgent.ps1 `
    -Engine dsc `
    -ConfigUrl https://dsc.h-edu.online/v3/baseline.dsc.yaml `
    -IntervalMinutes 30 -VerifyChecksum
```

The agent auto-downloads `dsc.exe` (pinned `-DscVersion`, default v3.2.3) into
`C:\Program Files\DSC` if it isn't already on PATH.

## What the installer does

Registers a scheduled task **`DscV3PullAgent`** running as SYSTEM at startup and
every `-IntervalMinutes` (drift correction — re-applies the config each run).
Test it immediately:

```powershell
Start-ScheduledTask -TaskName DscV3PullAgent
Get-Content C:\ProgramData\DscV3Pull\pull-agent.log -Tail 20
Get-Content C:\ProgramData\DscV3Pull\last-report.json
```

## Hosting the config (server side)

The config is just a static file. Serve it over the existing domain, e.g. an
nginx `location /v3/` that returns files from `/var/lib/tug/v3/`, and drop a
`<file>.sha256` next to it (uppercase SHA-256) so the agent can verify:

```bash
install -m644 baseline.winget.yaml /var/lib/tug/v3/
sha256sum /var/lib/tug/v3/baseline.winget.yaml | awk '{print toupper($1)}' \
    > /var/lib/tug/v3/baseline.winget.yaml.sha256
```

Optionally pass `-ReportUrl https://dsc.h-edu.online/v3/report` to POST each run's
JSON result back (add a matching endpoint/handler if you want them collected like
the v1/v2 reports).

## Status

Built and ready; **runtime-validate on a healthy node** — the exact resource
YAML (adapter type names, File settings) should be confirmed with a live
`dsc config set` / `winget configure` before fleet rollout. dsc.exe releases:
<https://github.com/PowerShell/DSC/releases>.
