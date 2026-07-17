Configuration SystemReport
{
    Node localhost
    {
        File ReportDir {
            DestinationPath = "C:\DSC\Reports"
            Type = "Directory"
            Ensure = "Present"
        }

        Script CollectReport {
            GetScript = {
                $latest = Get-ChildItem "C:\DSC\Reports\*.json" -ErrorAction SilentlyContinue |
                    Sort-Object LastWriteTime -Descending | Select-Object -First 1
                @{ Result = if ($latest -and ((Get-Date) - $latest.LastWriteTime).TotalMinutes -lt 60) {
                    "Fresh" } else { "Stale" } }
            }

            SetScript = {
                $report = @{
                    ComputerName = $env:COMPUTERNAME
                    Time = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
                    OSVersion = (Get-WmiObject Win32_OperatingSystem).Caption
                    OSBuild = (Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion").CurrentBuildNumber
                    PowerShell = $PSVersionTable.PSVersion.ToString()
                    RAM_GB = [math]::Round((Get-WmiObject Win32_OperatingSystem).TotalVisibleMemorySize / 1MB, 2)
                }

                # .NET versions
                $net = @()
                "v2.0.50727", "v3.0", "v3.5", "v4.0.30319" | ForEach-Object {
                    if (Test-Path "HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\$_") {
                        $net += $_
                    }
                }
                $report.DotNet = $net -join ", "

                # Software (top 15)
                $soft = @()
                @("HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                  "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall") |
                ForEach-Object {
                    if (Test-Path $_) {
                        Get-ChildItem $_ | ForEach-Object {
                            $n = (Get-ItemProperty $_.PSPath -Name DisplayName -ErrorAction SilentlyContinue).DisplayName
                            if ($n -and -not $n.StartsWith("{")) {
                                $soft += @{Name = $n; Version = (Get-ItemProperty $_.PSPath -Name DisplayVersion -ErrorAction SilentlyContinue).DisplayVersion}
                            }
                        }
                    }
                }
                $report.Software = $soft | Sort-Object Name | Select-Object -First 15

                # Antivirus
                $av = @()
                Get-WmiObject -Namespace "root\SecurityCenter2" -Class AntiVirusProduct 2>$null | ForEach-Object {
                    $av += @{Name = $_.displayName; Active = ($_.productState -band 4096)}
                }
                $report.AntiVirus = $av

                # Firewall
                $fw = @{}
                Get-NetFirewallProfile | ForEach-Object {
                    $fw[$_.Name] = @{Enabled = $_.Enabled}
                }
                $report.Firewall = $fw

                # Services
                $svc = @{}
                "WinRM", "BITS", "W32Time", "EventLog", "MpsSvc", "Dhcp" | ForEach-Object {
                    $s = Get-Service $_ -ErrorAction SilentlyContinue
                    if ($s) { $svc[$_] = $s.Status.ToString() }
                }
                $report.Services = $svc

                # Save
                $report | ConvertTo-Json | Out-File "C:\DSC\Reports\SystemReport_$(Get-Date -Format 'yyyyMMdd_HHmmss').json" -Encoding UTF8
            }

            TestScript = {
                $latest = Get-ChildItem "C:\DSC\Reports\*.json" -ErrorAction SilentlyContinue |
                    Sort-Object LastWriteTime -Descending | Select-Object -First 1
                if ($latest -and ((Get-Date) - $latest.LastWriteTime).TotalMinutes -lt 60) {
                    $true
                } else {
                    $false
                }
            }

            DependsOn = "[File]ReportDir"
        }
    }
}
