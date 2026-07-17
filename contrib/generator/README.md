# Fleet config generator

Author desired state **once**, emit every format a heterogeneous Windows fleet
needs. There is no off-the-shelf tool that produces MOF **and** DSC v3 **and**
WinGet from one source (the ecosystem is v3-centric and only converts MOF→v3),
so this is a small, focused generator.

```
neutral.yaml  (a list of classic DSC resources)
    │
    ├─►  <Name>.ps1         → compile → <Name>.mof     v1/v2 (WMF 4.0/5.1, old boxes)
    ├─►  <Name>.dsc.yaml    (dsc config set)           Server 2016/2019/2022
    └─►  <Name>.winget.yaml (winget configure)         Win10/11, Server 2025
```

## Why one source can drive all three

All three formats ultimately run the **same classic PowerShell DSC resources** —
MOF natively, DSC v3 and WinGet through the Windows PowerShell adapter. So a
`File` resource has the **identical** properties (`DestinationPath`, `Contents`,
`Ensure`, …) everywhere. The generator only changes the *wrapper*; it's a
re-wrapper, not a translator.

## Usage

```bash
pip install pyyaml
python3 generate-fleet-config.py neutral.example.yaml -o out
```

Produces `out/Baseline.ps1`, `out/Baseline.dsc.yaml`, `out/Baseline.winget.yaml`.

### The neutral spec

```yaml
name: Baseline
resources:
  - type: File                              # classic resource name
    name: HelloFile                         # instance id
    module: PSDesiredStateConfiguration     # optional; this is the default
    properties:                             # identical across all outputs
      DestinationPath: C:\TugTest\hello.txt
      Contents: "managed by the fleet baseline"
      Ensure: Present
      Type: File
  - type: Registry
    name: FleetMarker
    properties:
      Key: HKLM\SOFTWARE\Fleet
      ValueName: Baseline
      ValueData: applied
      ValueType: String
      Ensure: Present
```

## Publishing what it emits

```bash
# --- old boxes (v1/v2): compile the .ps1 to MOF on a Windows node, then publish ---
#   (PowerShell) .  .\Baseline.ps1 ;  Baseline -OutputPath .\out
tug-publish out/localhost.mof Baseline                    # v2, addressed by name
tug-publish out/localhost.mof 11111111-2222-...-555        # v1, addressed by ConfigurationId GUID

# --- modern boxes (v3/winget): host the YAMLs; the pull agent fetches them ---
install -m644 Baseline.dsc.yaml Baseline.winget.yaml /var/lib/tug/v3/
for f in /var/lib/tug/v3/Baseline.*.yaml; do
    sha256sum "$f" | awk '{print toupper($1)}' > "$f.sha256"
done
```

Point each node at the right target: legacy LCM → the MOF (see
[`../docs/DEPLOY.md`](../docs/DEPLOY.md)); modern → the pull agent with the
matching YAML (see [`../dsc-v3/`](../dsc-v3/)).

## Scope & limits (honest)

- **Classic PSDSC resources only** — File, Registry, Service, WindowsFeature,
  Script, Environment, Archive, etc. Their properties are uniform across formats,
  which is exactly what makes one-source generation clean.
- **Native DSC v3-only resources** (`Microsoft.Windows/Registry`, etc.) have a
  different schema and no MOF equivalent — author those directly as a DSC v3
  document; they can't be part of the universal path.
- **MOF still needs a Windows compile step** (the `.ps1` → `Start-DscConfiguration`
  compile). The two YAML outputs are pure text and need nothing.
- Property values are rendered for common scalar types (string/int/bool/list).
  Deeply nested/embedded-instance properties (e.g. credentials) should be
  reviewed by hand in the generated files.
