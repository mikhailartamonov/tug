"""Reusable WinRM helpers for a Windows DSC test node.

Configure the target via environment variables:
    WINRM_HOST   e.g. 203.0.113.10
    WINRM_USER   default: Administrator
    WINRM_PASS   the account password
"""
import base64
import os
from winrm.protocol import Protocol

HOST = os.environ.get("WINRM_HOST", "")
USER = os.environ.get("WINRM_USER", "Administrator")
PASS = os.environ.get("WINRM_PASS", "")

if not HOST or not PASS:
    raise SystemExit("Set WINRM_HOST and WINRM_PASS environment variables.")


def _proto():
    return Protocol(
        endpoint=f'http://{HOST}:5985/wsman',
        transport='ntlm',
        username=USER,
        password=PASS,
    )


def run_ps(script, timeout=180):
    """Run a PowerShell script (base64-encoded) and return (stdout, stderr, rc)."""
    p = _proto()
    b64 = base64.b64encode(script.encode('utf-16-le')).decode('ascii')
    shell = p.open_shell()
    try:
        cmd = p.run_command(shell, f"powershell -NoProfile -NonInteractive -EncodedCommand {b64}")
        out, err, rc = p.get_command_output(shell, cmd)
    finally:
        p.close_shell(shell)
    return out.decode('utf-8', errors='ignore'), err.decode('utf-8', errors='ignore'), rc


def upload_text(remote_path, content, chunk=1200):
    """Upload a text file to the Windows node via chunked base64.

    Splits the base64 into small pieces to stay under the cmd.exe command-line
    length limit, appends each to a staging file, then decodes at the end.
    """
    b64 = base64.b64encode(content.encode('utf-8')).decode('ascii')
    stage = remote_path + ".b64"

    # 1. Ensure dir exists and truncate staging file
    out, err, rc = run_ps(f"""
$dir = Split-Path '{remote_path}'
if (-not (Test-Path $dir)) {{ New-Item -ItemType Directory -Force -Path $dir | Out-Null }}
Set-Content -Path '{stage}' -Value '' -NoNewline -Encoding Ascii
""")
    # 2. Append chunks
    for i in range(0, len(b64), chunk):
        piece = b64[i:i + chunk]
        out, err, rc = run_ps(
            f"Add-Content -Path '{stage}' -Value '{piece}' -NoNewline -Encoding Ascii")
    # 3. Decode staging file -> target, remove staging
    return run_ps(f"""
$b64 = Get-Content -Path '{stage}' -Raw
$bytes = [Convert]::FromBase64String($b64)
[IO.File]::WriteAllBytes('{remote_path}', $bytes)
Remove-Item '{stage}' -Force
Write-Host "WROTE {remote_path} ($($bytes.Length) bytes)"
""")


def download_bytes(remote_path):
    """Download a file from the Windows node, return raw bytes."""
    script = f"[Convert]::ToBase64String([IO.File]::ReadAllBytes('{remote_path}'))"
    out, err, rc = run_ps(script)
    b64 = out.strip()
    return base64.b64decode(b64) if b64 else b""
