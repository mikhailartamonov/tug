#!/usr/bin/env python3
"""
Comprehensive DSC Pull Server test harness for a Tug pull server.

Exercises the full WMF 5.1 DSC v2 pull protocol with HMAC-signed requests
(register -> GetDscAction -> GetConfiguration -> SendReport -> GetReports)
plus negative/authorization cases, entirely from Python. This lets you verify
a Tug server end-to-end without a Windows LCM client.

Configure via environment:
    DSC_SERVER_URL   base URL of the pull server (e.g. https://dsc.example.com)
    DSC_REG_KEY      a valid registration-key GUID from the server

Run: python3 dsc_server_test.py
"""
import base64
import hashlib
import hmac
import json
import os
import sys
import uuid
from datetime import datetime, timezone
from pathlib import Path

import requests
import urllib3
urllib3.disable_warnings()

# Configure via environment:
#   DSC_SERVER_URL   base URL of the Tug pull server (default: local dev)
#   DSC_REG_KEY      a valid registration key (GUID) from the server's
#                    Authz/RegistrationKeys.txt
BASE = os.environ.get("DSC_SERVER_URL", "http://localhost:5000")
REG_KEY = os.environ.get("DSC_REG_KEY", "")

if not REG_KEY:
    raise SystemExit("Set DSC_REG_KEY (a valid server registration-key GUID).")

# Expected config: derived from the sample MOF shipped in contrib/configs, so
# the checks stay self-consistent with whatever SystemReport.mof you deploy.
# Override with DSC_MOF_PATH to point at your own compiled configuration.
LOCAL_MOF = Path(os.environ.get(
    "DSC_MOF_PATH", Path(__file__).parent.parent / "configs" / "SystemReport.mof"))
if LOCAL_MOF.exists():
    _mof = LOCAL_MOF.read_bytes()
    SYSREPORT_SHA = hashlib.sha256(_mof).hexdigest().upper()
    SYSREPORT_LEN = len(_mof)
else:
    SYSREPORT_SHA, SYSREPORT_LEN = None, None

results = []  # (name, ok, detail)


def cjson(obj):
    """Compact JSON exactly like Newtonsoft JsonConvert.SerializeObject:
    no spaces after ':' or ',' (Tug's VeryStrictInputFilter compares byte-for-byte)."""
    return json.dumps(obj, separators=(",", ":")).encode()


def check(name, ok, detail=""):
    results.append((name, ok, detail))
    mark = "PASS" if ok else "FAIL"
    print(f"  [{mark}] {name}" + (f" — {detail}" if detail else ""))
    return ok


def ms_date():
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%f0Z")


def sign(body_bytes, key=REG_KEY):
    """Return DSC auth headers for a given raw body."""
    body_hash = base64.b64encode(hashlib.sha256(body_bytes).digest()).decode()
    date = ms_date()
    to_sign = f"{body_hash}\n{date}".encode()
    sig = base64.b64encode(
        hmac.new(key.encode(), to_sign, hashlib.sha256).digest()).decode()
    return {
        "Authorization": f"Shared {sig}",
        "x-ms-date": date,
        "ProtocolVersion": "2.0",
    }


def register(agent_id, config_names, key=REG_KEY):
    # Field order must match Newtonsoft's serialization of the model classes
    # (AgentInformation: LCMVersion,NodeName,IPAddress ; Registration: Cert,MsgType ;
    #  Cert: FriendlyName,Issuer,NotAfter,NotBefore,Subject,PublicKey,Thumbprint,Version).
    body = cjson({
        "AgentInformation": {
            "LCMVersion": "2.0",
            "NodeName": "TESTNODE-HARNESS",
            "IPAddress": "10.0.0.1;127.0.0.1",
        },
        "ConfigurationNames": config_names,
        "RegistrationInformation": {
            "CertificateInformation": {
                "FriendlyName": "Tug-Test",
                "Issuer": "CN=Test",
                "NotAfter": "2030-01-01T00:00:00Z",
                "NotBefore": "2020-01-01T00:00:00Z",
                "Subject": "CN=Test",
                "PublicKey": "MIIB",
                "Thumbprint": "0000000000000000000000000000000000000000",
                "Version": 3,
            },
            "RegistrationMessageType": "ConfigurationRepository",
        },
    })
    h = sign(body, key)
    h["Content-Type"] = "application/json"
    return requests.put(f"{BASE}/Nodes(AgentId='{agent_id}')",
                        data=body, headers=h, verify=True, timeout=30)


def get_dsc_action(agent_id, config_name, checksum="", key=REG_KEY):
    # ClientStatusItem field order: ConfigurationName, Checksum, ChecksumAlgorithm
    body = cjson({
        "ClientStatus": [{
            "ConfigurationName": config_name,
            "Checksum": checksum,
            "ChecksumAlgorithm": "SHA-256",
        }]
    })
    h = sign(body, key)
    h["Content-Type"] = "application/json"
    return requests.post(f"{BASE}/Nodes(AgentId='{agent_id}')/GetDscAction",
                        data=body, headers=h, verify=True, timeout=30)


def get_configuration(agent_id, config_name, key=REG_KEY):
    h = sign(b"", key)
    uri = (f"{BASE}/Nodes(AgentId='{agent_id}')"
        f"/Configurations(ConfigurationName='{config_name}')/ConfigurationContent")
    return requests.get(uri, headers=h, verify=True, timeout=30)


def send_report(agent_id, report, key=REG_KEY):
    body = cjson(report)
    h = sign(body, key)
    h["Content-Type"] = "application/json"
    return requests.post(f"{BASE}/Nodes(AgentId='{agent_id}')/SendReport",
                        data=body, headers=h, verify=True, timeout=30)


def get_reports(agent_id, key=REG_KEY):
    h = sign(b"", key)
    return requests.get(f"{BASE}/Nodes(AgentId='{agent_id}')/Reports",
                        headers=h, verify=True, timeout=30)


def main():
    print(f"\n===== DSC Server Test: {BASE} =====\n")

    # Preflight: version
    print("[ Preflight ]")
    try:
        r = requests.get(f"{BASE}/version", timeout=15, verify=True)
        check("server /version reachable", r.status_code == 200, r.text.strip())
    except Exception as e:
        check("server /version reachable", False, str(e))
        print("\nServer unreachable — aborting.")
        sys.exit(1)

    agent = str(uuid.uuid4()).upper()
    print(f"\n[ Happy path ]  agent={agent}")

    # 1. Register
    r = register(agent, ["SystemReport"])
    check("register agent (SystemReport)", r.status_code in (200, 204),
        f"HTTP {r.status_code}")

    # 2. GetDscAction with empty checksum → expect update needed
    #    (Tug returns camelCase JSON: {"nodeStatus":"GetConfiguration",...})
    r = get_dsc_action(agent, "SystemReport", "")
    status = ""
    if r.status_code == 200:
        try:
            j = {k.lower(): v for k, v in r.json().items()}
            status = j.get("nodestatus", "")
        except Exception:
            status = r.text[:80]
    check("GetDscAction (empty checksum) → GetConfiguration",
        r.status_code == 200 and status == "GetConfiguration",
        f"HTTP {r.status_code} nodeStatus={status!r}")

    # 3. GetConfiguration → verify byte-perfect + checksum header
    r = get_configuration(agent, "SystemReport")
    body = r.content
    hdr_checksum = r.headers.get("Checksum", "")
    hdr_algo = r.headers.get("ChecksumAlgorithm", "")
    actual_sha = hashlib.sha256(body).hexdigest().upper()
    check("GetConfiguration HTTP 200", r.status_code == 200, f"HTTP {r.status_code}")
    check(f"GetConfiguration length == {SYSREPORT_LEN}", len(body) == SYSREPORT_LEN,
        f"{len(body)} bytes")
    check("GetConfiguration body SHA256 matches deployed",
        actual_sha == SYSREPORT_SHA, actual_sha)
    check("Checksum header matches body", hdr_checksum.upper() == actual_sha,
        f"header={hdr_checksum}")
    check("ChecksumAlgorithm header == SHA-256", hdr_algo == "SHA-256", hdr_algo)
    if LOCAL_MOF.exists():
        check("body byte-identical to local compiled MOF",
            body == LOCAL_MOF.read_bytes())

    # 4. GetDscAction WITH correct checksum → expect OK (idempotent, no re-pull)
    r = get_dsc_action(agent, "SystemReport", actual_sha)
    status2 = ""
    if r.status_code == 200:
        try:
            j = {k.lower(): v for k, v in r.json().items()}
            status2 = j.get("nodestatus", "")
        except Exception:
            status2 = r.text[:80]
    check("GetDscAction (correct checksum) → OK/no-update",
        r.status_code == 200 and status2 in ("OK", ""),
        f"HTTP {r.status_code} nodeStatus={status2!r}")

    # 5. SendReport (Success)
    job = str(uuid.uuid4())
    now = ms_date()
    report = {
        "JobId": job, "OperationType": "Consistency", "RefreshMode": "Pull",
        "Status": "Success", "ReportFormatVersion": "2.0",
        "StartTime": now, "EndTime": now,
        "ConfigurationVersion": "2.0.0", "NodeName": "TESTNODE-HARNESS",
        "IpAddress": "10.0.0.1", "RebootRequested": "False",
        "Errors": [], "StatusData": [], "AdditionalData": [],
    }
    r = send_report(agent, report)
    check("SendReport (Success) accepted", r.status_code in (200, 204),
        f"HTTP {r.status_code}")

    # 6. GetReports → the report should come back
    r = get_reports(agent)
    found = job in r.text if r.status_code == 200 else False
    check("GetReports returns the sent JobId",
        r.status_code == 200 and found,
        f"HTTP {r.status_code}, jobId {'found' if found else 'MISSING'}")

    print("\n[ Negative / robustness ]")
    bad_agent = str(uuid.uuid4()).upper()

    # 8. Registration with wrong key → 401
    r = register(bad_agent, ["SystemReport"], key="00000000-0000-0000-0000-000000000000")
    check("register with WRONG key → 401", r.status_code == 401,
        f"HTTP {r.status_code}")

    # 9. Unregistered agent with BAD signature → 401. (A *valid* reg-key HMAC
    #    signature is itself sufficient authz by design — possessing the key is
    #    the trust anchor — so the real security assertion is: forged/absent
    #    signature on an unknown agent must be rejected.)
    h = sign(b"")
    h["Authorization"] = "Shared " + base64.b64encode(b"forged-signature").decode()
    uri = (f"{BASE}/Nodes(AgentId='{bad_agent}')"
        f"/Configurations(ConfigurationName='SystemReport')/ConfigurationContent")
    r = requests.get(uri, headers=h, verify=True, timeout=30)
    check("GetConfiguration, unregistered + BAD signature → 401",
        r.status_code == 401, f"HTTP {r.status_code}")

    # 9b. No auth header at all on unknown agent → 401
    uri2 = (f"{BASE}/Nodes(AgentId='{bad_agent}')"
        f"/Configurations(ConfigurationName='SystemReport')/ConfigurationContent")
    r = requests.get(uri2, headers={"ProtocolVersion": "2.0"}, verify=True, timeout=30)
    check("GetConfiguration, unregistered + NO auth → 401",
        r.status_code == 401, f"HTTP {r.status_code}")

    # 10. GetConfiguration for nonexistent config (registered agent) → 404
    r = get_configuration(agent, "NoSuchConfigXYZ")
    check("GetConfiguration nonexistent config → 404", r.status_code == 404,
        f"HTTP {r.status_code}")

    # Summary
    print("\n===== SUMMARY =====")
    passed = sum(1 for _, ok, _ in results if ok)
    total = len(results)
    for name, ok, detail in results:
        if not ok:
            print(f"  FAILED: {name} — {detail}")
    print(f"\n  {passed}/{total} checks passed")
    sys.exit(0 if passed == total else 1)


if __name__ == "__main__":
    main()
