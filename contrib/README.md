# contrib — .NET 8 deployment tooling & tests

This directory holds tooling that accompanies the .NET 8 port of Tug: a
protocol test harness, a sample DSC configuration, and deployment notes.
None of it is required to build the server — it's here to make the port
reproducible and verifiable.

```
contrib/
├── tools/
│   ├── dsc_server_test.py   # full DSC pull-protocol test harness (HMAC, pure Python)
│   └── winrm_helper.py      # helpers to compile MOFs on a Windows node over WinRM
├── configs/
│   ├── SystemReport.ps1     # a DSC configuration that reports host inventory
│   └── SystemReport.mof     # the compiled MOF (sample; regenerate from the .ps1)
└── docs/
    ├── NET8-PORT.md         # what the port changed and why
    └── DEPLOY.md            # running behind nginx + a systemd unit
```

## Verifying a server (no Windows client needed)

The test harness speaks the WMF 5.1 DSC v2 pull protocol directly, signing
each request with the registration-key HMAC. It exercises the happy path
byte-for-byte and the authorization edge cases.

```bash
pip install requests pywinrm
export DSC_SERVER_URL="https://your-pull-server"
export DSC_REG_KEY="<a-registration-key-guid-from-the-server>"
python3 contrib/tools/dsc_server_test.py
```

Expected output ends with `17/17 checks passed`, covering:

- register → GetDscAction (empty checksum → `GetConfiguration`)
- GetConfiguration: HTTP 200, `Checksum`/`ChecksumAlgorithm` headers, body
  byte-identical to the deployed MOF
- GetDscAction with the correct checksum → `OK` (idempotent, no re-pull)
- SendReport → GetReports round-trip
- negatives: wrong registration key → 401, forged/absent signature on an
  unknown agent → 401, unknown configuration → 404

## Compiling the sample MOF

`SystemReport.mof` is produced from `SystemReport.ps1` on any Windows box with
PowerShell 5.1+:

```powershell
. .\SystemReport.ps1
SystemReport -OutputPath .\out
# out\localhost.mof  ->  rename to SystemReport.mof and deploy to the server
```

`winrm_helper.py` automates that against a remote node (set `WINRM_HOST`,
`WINRM_USER`, `WINRM_PASS`).
