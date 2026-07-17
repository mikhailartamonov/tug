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
            RefreshMode          = 'Pull'
            RefreshFrequencyMins = 30
            ConfigurationMode    = 'ApplyAndAutoCorrect'
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
Update-DscConfiguration -Wait -Verbose
```
