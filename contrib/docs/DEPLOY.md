# Deploying the .NET 8 Tug server

A minimal production layout: Kestrel on loopback, nginx terminating TLS in
front, a systemd unit to keep it running. Adjust paths/hostnames to taste.
No secrets belong in this repo — keep registration keys, TLS keys, and node
credentials out of version control.

## Build & publish

```bash
dotnet publish src/TugDSC.Server.WebAppHost/TugDSC.Server.WebAppHost.csproj \
    -c Release -o /opt/tug
# framework-dependent: the host needs the aspnetcore-runtime-8.0 package
```

> Caveat: `dotnet publish` bundles a template `appsettings.json` with
> placeholder `_IGNORE/...` paths. Keep your production `appsettings.json`
> (with real `/var/lib/tug/*` paths) and don't let a redeploy overwrite it.

## File store (`/var/lib/tug`)

```
Authz/RegistrationKeys.txt   one registration-key GUID per line
Registrations/               <agentId>.json written on register
Configuration/               <Name>.mof (+ <Name>.mof.checksum)
Configuration/SHARED/        deploy each <Name>.mof here too — see note
Modules/                     <Name>_<Version>.zip (+ .checksum)
Reports/                     <agentId>/<jobId>.json
```

> Note: `BasicDscHandler.GetDscAction` checks `Configuration/SHARED/<Name>.mof`
> for existence/checksum, while `GetConfiguration` serves from
> `Configuration/<Name>.mof`. Deploy each config to **both** paths.

Checksum files are the uppercased SHA-256 of the `.mof`:

```bash
sha256sum SystemReport.mof | awk '{print toupper($1)}' > SystemReport.mof.checksum
```

## systemd unit (sketch)

```ini
[Unit]
Description=Tug DSC Pull Server (.NET 8)
After=network.target

[Service]
WorkingDirectory=/opt/tug
ExecStart=/opt/tug/TugDSC.Server.WebAppHost
Environment=ASPNETCORE_URLS=http://127.0.0.1:5000
Restart=on-failure
MemoryMax=256M

[Install]
WantedBy=multi-user.target
```

## nginx front (sketch)

```nginx
server {
    listen 443 ssl;
    server_name dsc.example.com;

    ssl_certificate     /etc/ssl/origin.pem;   # e.g. a Cloudflare Origin cert
    ssl_certificate_key /etc/ssl/origin.key;   # 0600, root-only, NOT in git

    location / {
        proxy_pass         http://127.0.0.1:5000;
        proxy_set_header   Host $host;
        proxy_set_header   X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header   X-Forwarded-Proto $scheme;
    }
}
```

## Point a Windows node at it

```powershell
[DSCLocalConfigurationManager()]
configuration TugLCM {
    Node localhost {
        Settings {
            RefreshMode                    = 'Pull'
            RefreshFrequencyMins           = 30
            ConfigurationModeFrequencyMins = 15
            ConfigurationMode              = 'ApplyAndAutoCorrect'
            RebootNodeIfNeeded             = $false
        }
        ConfigurationRepositoryWeb Tug {
            ServerURL          = 'https://dsc.example.com'
            RegistrationKey    = '<registration-key-guid>'
            ConfigurationNames = @('SystemReport')
        }
        ReportServerWeb TugReport {
            ServerURL       = 'https://dsc.example.com'
            RegistrationKey = '<registration-key-guid>'
        }
    }
}
TugLCM -OutputPath C:\DSC\LCM
Set-DscLocalConfigurationManager -Path C:\DSC\LCM
Update-DscConfiguration -Wait -Verbose      # force one pull now, don't wait for the timer
```

### How often a node checks in

Registration only settles *which* server and *which* configuration a node
wants. The **cadence** is set here in the LCM meta-configuration `Settings`
block (applied by `Set-DscLocalConfigurationManager`), and it's driven by two
independent timers:

| Setting | Controls | Min / default |
|---------|----------|---------------|
| `RefreshFrequencyMins` | how often the node **contacts the server** to download the latest configuration + modules | min **30**, default 30 |
| `ConfigurationModeFrequencyMins` | how often the node runs a **consistency check** — re-applies its cached configuration and (in AutoCorrect) fixes drift | min **15**, default 15 |

```
every ConfigurationModeFrequencyMins (15m):  consistency check of the cached config → correct drift
every RefreshFrequencyMins           (30m):  pull the latest config/modules from the server → apply
```

- `RefreshFrequencyMins` must be a **multiple** of `ConfigurationModeFrequencyMins`
  (the LCM rounds it up otherwise). The classic pair is 15 / 30.
- `ConfigurationMode` decides what a consistency check *does*:
  `ApplyOnly` (apply once), `ApplyAndMonitor` (report drift, don't fix), or
  `ApplyAndAutoCorrect` (re-apply to fix drift each check).
- The LCM realizes these timers as scheduled tasks under
  `\Microsoft\Windows\Desired State Configuration`.

Useful commands on the node:

```powershell
Get-DscLocalConfigurationManager    # inspect the current cadence + mode
Update-DscConfiguration -Wait -Verbose                          # force a pull now
Invoke-CimMethod -Namespace root/Microsoft/Windows/DesiredStateConfiguration `
    -ClassName MSFT_DSCLocalConfigurationManager `
    -MethodName PerformRequiredConfigurationChecks -Arguments @{ Flags = [uint32]1 }  # force a consistency check now
```

## Onboard a legacy WMF 4.0 (v1) node

PowerShell **4.0** nodes speak the older ConfigurationId-based protocol — no
registration, no `ConfigurationNames`. Address the config by a GUID and publish
the MOF under that GUID (`tug-publish MyConfig.mof <guid>`). The meta-config uses
the old `LocalConfigurationManager` block with a `WebDownloadManager`, and the
`ServerUrl` **must end in a path** (the classic `PSDSCPullServer.svc`) or the v4
downloader fails constructing its request URI:

```powershell
$cfgId = '11111111-2222-3333-4444-555555555555'   # your ConfigurationId

Configuration NodeLCMv1 {
    Node localhost {
        LocalConfigurationManager {
            ConfigurationID           = $cfgId
            RefreshMode               = 'Pull'
            RefreshFrequencyMins      = 15
            ConfigurationModeFrequencyMins = 30
            RebootNodeIfNeeded        = $false
            DownloadManagerName       = 'WebDownloadManager'
            DownloadManagerCustomData = @{
                ServerUrl               = 'https://dsc.example.com/PSDSCPullServer.svc'
                AllowUnsecureConnection = 'false'
            }
        }
    }
}
NodeLCMv1 -OutputPath C:\LcmV1
Set-DscLocalConfigurationManager -Path C:\LcmV1
# force a pull (PS 4.0 has no Update-DscConfiguration):
Invoke-CimMethod -Namespace root/Microsoft/Windows/DesiredStateConfiguration `
    -ClassName MSFT_DSCLocalConfigurationManager `
    -MethodName PerformRequiredConfigurationChecks -Arguments @{ Flags = [uint32]1 }
```

The server serves the same store to both protocols; a v1 node just fetches
`<ConfigurationId>.mof`. See [`NET8-PORT.md`](NET8-PORT.md) for the wire details.

> **TLS note.** The v4 downloader negotiates older TLS. It traverses Cloudflare
> fine (the edge accepts TLS 1.0 and the downloader sends SNI), so v1 and v2
> nodes can share one proxied hostname — no separate endpoint needed.
