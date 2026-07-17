#!/usr/bin/env python3
"""
Fleet config generator — author intent once, emit every format the fleet needs.

A heterogeneous Windows fleet needs the *same* desired state in three shapes:

    neutral.yaml  (a list of classic DSC resources)
        |
        +--> <Name>.ps1         -> compile -> <Name>.mof   (WMF 4.0/5.1 v1/v2, old boxes)
        +--> <Name>.dsc.yaml    (dsc.exe config set)       (Server 2016/2019/2022)
        +--> <Name>.winget.yaml (winget configure)         (Win10/11, Server 2025)

This works because all three ultimately run the *same* classic PowerShell DSC
resources (MOF natively; DSC v3 and WinGet via the Windows PowerShell adapter),
so a resource's properties are identical across formats. The generator is a
re-wrapper, not a translator.

Scope: classic PSDSC resources (File, Registry, Service, WindowsFeature, Script,
Environment, ...). Native DSC-v3-only resources have no MOF equivalent and are
out of scope — author those as a DSC v3 document directly.

Usage:
    python3 generate-fleet-config.py neutral.yaml [-o OUTDIR]

Neutral spec (YAML):
    name: Baseline
    resources:
      - type: File                 # classic resource name
        name: HelloFile            # instance id
        module: PSDesiredStateConfiguration   # optional (this is the default)
        properties:
          DestinationPath: C:\\TugTest\\hello.txt
          Contents: "managed by fleet baseline"
          Ensure: Present
          Type: File
"""
import argparse
import sys
from pathlib import Path

try:
    import yaml
except ImportError:
    sys.exit("PyYAML required:  pip install pyyaml")

DEFAULT_MODULE = "PSDesiredStateConfiguration"


def load_spec(path):
    spec = yaml.safe_load(Path(path).read_text())
    if not spec or "name" not in spec or "resources" not in spec:
        sys.exit("spec must have 'name' and 'resources'")
    for r in spec["resources"]:
        r.setdefault("module", DEFAULT_MODULE)
        r.setdefault("name", r["type"])
        r.setdefault("properties", {})
    return spec


# ---- emitters -------------------------------------------------------------

def emit_winget(spec):
    """WinGet Configuration 0.2 document (winget configure --file)."""
    resources = []
    for r in spec["resources"]:
        resources.append({
            "resource": f"{r['module']}/{r['type']}",
            "id": r["name"],
            "directives": {"description": r["name"], "allowPrerelease": True},
            "settings": r["properties"],
        })
    doc = {"properties": {"configurationVersion": "0.2.0", "resources": resources}}
    header = "# yaml-language-server: $schema=https://aka.ms/configuration-dsc-schema/0.2\n"
    return header + yaml.safe_dump(doc, sort_keys=False, default_flow_style=False)


def emit_v3(spec):
    """DSC v3 configuration document (dsc config set --file), via the
    Windows PowerShell adapter so classic resources run unchanged."""
    adapted = [{
        "name": r["name"],
        "type": f"{r['module']}/{r['type']}",
        "properties": r["properties"],
    } for r in spec["resources"]]
    doc = {
        "$schema": "https://aka.ms/dsc/schemas/v3/bundled/config/document.json",
        "resources": [{
            "name": "windows-powershell",
            "type": "Microsoft.Windows/WindowsPowerShell",
            "properties": {"resources": adapted},
        }],
    }
    return yaml.safe_dump(doc, sort_keys=False, default_flow_style=False)


def _ps_val(v):
    if isinstance(v, bool):
        return "$true" if v else "$false"
    if isinstance(v, (int, float)):
        return str(v)
    if isinstance(v, list):
        return "@(" + ", ".join(_ps_val(x) for x in v) + ")"
    return "'" + str(v).replace("'", "''") + "'"   # PS single-quote literal


def emit_ps1(spec):
    """A classic PowerShell DSC Configuration -> compile to MOF for v1/v2."""
    modules = sorted({r["module"] for r in spec["resources"]})
    lines = [f"Configuration {spec['name']}", "{"]
    for m in modules:
        lines.append(f"    Import-DscResource -ModuleName {m}")
    lines += ["", "    Node localhost", "    {"]
    for r in spec["resources"]:
        lines.append(f"        {r['type']} {r['name']}")
        lines.append("        {")
        width = max((len(k) for k in r["properties"]), default=0)
        for k, v in r["properties"].items():
            lines.append(f"            {k.ljust(width)} = {_ps_val(v)}")
        lines.append("        }")
    lines += ["    }", "}", ""]
    return "\n".join(lines)


def main():
    ap = argparse.ArgumentParser(description="Generate MOF/.ps1 + DSC v3 + WinGet from one neutral spec.")
    ap.add_argument("spec", help="neutral spec YAML")
    ap.add_argument("-o", "--outdir", default=".", help="output directory")
    args = ap.parse_args()

    spec = load_spec(args.spec)
    name = spec["name"]
    out = Path(args.outdir)
    out.mkdir(parents=True, exist_ok=True)

    targets = {
        f"{name}.ps1": emit_ps1(spec),
        f"{name}.dsc.yaml": emit_v3(spec),
        f"{name}.winget.yaml": emit_winget(spec),
    }
    for fn, content in targets.items():
        (out / fn).write_text(content)
        print(f"  wrote {out / fn}")

    print(f"""
Next:
  # legacy v1/v2 (old boxes) — compile the MOF on a Windows node, then publish:
  .  {name}.ps1 ;  {name} -OutputPath .\\out
  tug-publish out/localhost.mof {name}              # v2 (by name)
  tug-publish out/localhost.mof <ConfigurationId>   # v1 (by GUID)

  # modern boxes — host the YAMLs (served by the pull agent):
  install -m644 {name}.dsc.yaml {name}.winget.yaml /var/lib/tug/v3/
  # (add .sha256 sidecars like the other v3 configs)
""")


if __name__ == "__main__":
    main()
