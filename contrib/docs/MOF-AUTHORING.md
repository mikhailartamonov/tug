# Authoring & deploying MOF configurations

A practical, self-contained recipe for writing a DSC configuration, compiling
it to a `.mof`, and publishing it to this pull server. Written for **Windows
PowerShell 5.1** (the LCM that talks to a v2 pull server).

- [1. What a MOF is](#1-what-a-mof-is)
- [2. Write a configuration (`.ps1`)](#2-write-a-configuration-ps1)
- [3. Resources you'll actually use](#3-resources-youll-actually-use)
- [4. Compile to MOF](#4-compile-to-mof)
- [5. Publish to the server](#5-publish-to-the-server)
- [6. Point a node at it](#6-point-a-node-at-it)
- [7. Worked example: SystemReport](#7-worked-example-systemreport)
- [8. Pitfalls (read this)](#8-pitfalls-read-this)
- [9. One-shot cheat sheet](#9-one-shot-cheat-sheet)

> **Fast path.** Two helpers automate the mechanical parts of everything below:
> `contrib/tools/New-TugConfig.ps1` scaffolds a config and compiles it (Windows),
> and `contrib/tools/tug-publish` deploys the MOF to both store paths with the
> right checksum (server). This document explains what they do and how to do it
> by hand — read it once, then lean on the tools.

---

## 1. What a MOF is

A **MOF** (Managed Object Format) is the *compiled* form of a DSC
configuration. You write a configuration in PowerShell; compiling it produces a
`.mof` text document describing the exact resource instances a node must
realize. The pull server never runs PowerShell — it just stores and serves that
`.mof` (plus a checksum) to nodes that ask for it by name.

Flow:

```
 Configuration.ps1  --compile-->  <Name>.mof  --deploy-->  pull server
                                                                │
                                            node pulls <Name>.mof, applies it
```

## 2. Write a configuration (`.ps1`)

The skeleton is always the same: a `Configuration` block containing one or more
`Node` blocks, each holding resource declarations.

```powershell
Configuration WebServer
{
    # Optional: import resource modules you use (built-ins need no import)
    Import-DscResource -ModuleName PSDesiredStateConfiguration

    Node localhost           # the target; 'localhost' is fine for pull MOFs
    {
        WindowsFeature IIS {
            Name   = 'Web-Server'
            Ensure = 'Present'
        }

        File SiteRoot {
            DestinationPath = 'C:\inetpub\wwwroot\app'
            Type            = 'Directory'
            Ensure          = 'Present'
            DependsOn       = '[WindowsFeature]IIS'
        }
    }
}
```

- The **configuration name** (`WebServer`) is what matters on the server — the
  published file must be `WebServer.mof` and the node must request `WebServer`.
- `DependsOn` orders resources: `'[ResourceType]Name'`.
- `Node localhost` is conventional for pull configs; the node applies whatever
  MOF it pulls regardless of the node name baked in.

## 3. Resources you'll actually use

**Built-in** (module `PSDesiredStateConfiguration`, always available):

| Resource        | For                                             |
|-----------------|-------------------------------------------------|
| `File`          | files & directories, content, copies            |
| `Registry`      | registry keys/values                            |
| `Service`       | Windows service state / start mode              |
| `WindowsFeature`| roles & features (Server)                       |
| `Environment`   | environment variables                           |
| `Script`        | arbitrary Get/Test/Set logic (escape hatch)     |

**`Script`** is the escape hatch when no resource fits — e.g. gathering
inventory. It has three script blocks:

```powershell
Script CollectInfo {
    # returns the current state (a hashtable with a 'Result' string)
    GetScript  = { @{ Result = (Get-Content C:\out\info.json -Raw) } }

    # $true  = already in desired state (Set is skipped)
    # $false = not compliant -> Set runs
    TestScript = { Test-Path C:\out\info.json }

    # makes it so
    SetScript  = {
        @{ os = (Get-CimInstance Win32_OperatingSystem).Caption } |
            ConvertTo-Json | Out-File C:\out\info.json -Encoding UTF8
    }
}
```

Rules of thumb for `Script`:
- `TestScript` **must** return a Boolean. If it always returns `$false`, `Set`
  runs every consistency check (fine for "always refresh" jobs like a report).
- Keep the blocks self-contained — variables from the enclosing scope aren't
  captured unless you use `$using:` and the config supports it.
- Heavy `Script` blocks slow every run; prefer real resources when one exists.

## 4. Compile to MOF

Dot-source the file (or run it), invoke the configuration by name, point it at
an output folder. **Clean the output folder first** so you never re-read a stale
`localhost.mof` from an earlier attempt.

```powershell
Remove-Item -Recurse -Force .\out -ErrorAction SilentlyContinue
. .\WebServer.ps1              # load the Configuration into the session
WebServer -OutputPath .\out    # compile -> .\out\localhost.mof
```

The result is `.\out\localhost.mof`. Rename it to `<ConfigurationName>.mof`
before publishing (`WebServer.mof` here).

> `New-TugConfig.ps1 -Name WebServer -Compile` does exactly this — clean folder,
> compile, rename to `WebServer.mof`, print the hash — and scaffolds the starter
> `.ps1` too. Use it to skip the boilerplate once you know what it's doing.

Sanity-check that it actually compiled what you meant (not a stale stub):

```powershell
Select-String -Path .\out\localhost.mof -Pattern 'instance of MSFT_' | Measure-Object
# and eyeball it: it should mention your resources, e.g. MSFT_ScriptResource
```

> No Windows box handy? `contrib/tools/winrm_helper.py` compiles a `.ps1` on a
> remote node over WinRM and downloads the resulting MOF (set `WINRM_HOST`,
> `WINRM_USER`, `WINRM_PASS`).

## 5. Publish to the server

The server's file store lives under `Configuration/`. A configuration named
`WebServer` needs **three** things in place:

```bash
CN=WebServer

# 1. the MOF, served on GetConfiguration
cp WebServer.mof  /var/lib/tug/Configuration/$CN.mof

# 2. the SAME MOF under SHARED/ — GetDscAction checks existence/checksum here
cp WebServer.mof  /var/lib/tug/Configuration/SHARED/$CN.mof

# 3. the checksum: uppercase SHA-256 of the .mof, as <Name>.mof.checksum
sha256sum /var/lib/tug/Configuration/$CN.mof | awk '{print toupper($1)}' \
    > /var/lib/tug/Configuration/$CN.mof.checksum
```

Why both paths: this server's `GetDscAction` looks in `Configuration/SHARED/`
to decide whether the node needs an update, while `GetConfiguration` serves the
byte stream from `Configuration/`. Deploy to **both** or the node either never
pulls or pulls a mismatched file.

> `tug-publish WebServer.mof WebServer` does all three steps (both copies + the
> uppercase checksum) and refuses a blank/stale file. Add `--verify` to run the
> protocol harness against the server right after publishing.

Modules (if your config needs non-built-in resources) go under `Modules/` as
`<Name>_<Version>.zip` with a matching `<...>.zip.checksum`.

## 6. Point a node at it

On the Windows node, set the LCM to pull mode and name the configuration. See
`DEPLOY.md` for the full meta-config; the key line is:

```powershell
ConfigurationNames = @('WebServer')   # must match <Name>.mof on the server
```

Then force a cycle:

```powershell
Update-DscConfiguration -Wait -Verbose
Get-DscConfigurationStatus
```

## 7. Worked example: SystemReport

`contrib/configs/SystemReport.ps1` is a real, ready-to-use configuration that
writes a host-inventory JSON (OS/build, .NET versions, installed software,
antivirus, firewall, key services) on every consistency check. It's a good
template for any "collect and report" job because it leans on a single `Script`
resource whose `TestScript` returns `$false` (so `Set` refreshes each run).

Compile and publish it exactly as above:

```powershell
Remove-Item -Recurse -Force .\out -ErrorAction SilentlyContinue
. .\SystemReport.ps1
SystemReport -OutputPath .\out
# -> .\out\localhost.mof  (rename to SystemReport.mof and publish per step 5)
```

The shipped `contrib/configs/SystemReport.mof` is the compiled result, and it's
what `contrib/tools/dsc_server_test.py` uses as its reference artifact when it
checks the server hands back a byte-identical file.

## 8. Pitfalls (read this)

- **Stale MOF.** If you don't clean the output folder, `WebServer -OutputPath`
  can leave an older `localhost.mof` in place and you'll ship the wrong thing.
  Always `Remove-Item -Recurse -Force .\out` first, then verify the content.
- **Both paths + checksum.** Missing `SHARED/` copy → node reports "up to date"
  and never pulls. Missing/typo'd `.mof.checksum`, or lowercase hex → the node
  rejects the download. The checksum is the **uppercase** SHA-256 of the exact
  bytes you deploy.
- **Name mismatch.** `<Name>.mof` on the server must equal a string in the
  node's `ConfigurationNames`. Case matters.
- **`TestScript` must return Boolean.** Returning a string/array makes the LCM
  misbehave. Wrap in `[bool]` if unsure.
- **Encoding.** `Out-File` for MOFs defaults to UTF-16 on PS 5.1 — that's fine
  for the compiler's own output; don't hand-edit a compiled MOF and re-save it
  in a different encoding, or the checksum (and parser) breaks.
- **Module dependencies.** If a config uses a non-built-in resource, the node
  must be able to pull that module from `Modules/` or it can't apply.

## 9. One-shot cheat sheet

```powershell
# --- on a Windows box: author + compile ---
Remove-Item -Recurse -Force .\out -ErrorAction SilentlyContinue
. .\MyConfig.ps1
MyConfig -OutputPath .\out
Get-FileHash .\out\localhost.mof -Algorithm SHA256   # note the hash
```

```bash
# --- on the server: publish ---
CN=MyConfig
install -m644 localhost.mof /var/lib/tug/Configuration/$CN.mof
install -m644 localhost.mof /var/lib/tug/Configuration/SHARED/$CN.mof
sha256sum /var/lib/tug/Configuration/$CN.mof | awk '{print toupper($1)}' \
    > /var/lib/tug/Configuration/$CN.mof.checksum
```

```bash
# --- verify end-to-end without a Windows client ---
export DSC_SERVER_URL="https://your-server" DSC_REG_KEY="<guid>"
DSC_MOF_PATH=./localhost.mof python3 contrib/tools/dsc_server_test.py
```
