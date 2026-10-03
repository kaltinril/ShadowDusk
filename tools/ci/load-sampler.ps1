# tools/ci/load-sampler.ps1: what the runner was doing while the integration tests ran (issue #373).
#
# Started in the background right before `dotnet test` on the Windows integration lane (see
# .github/workflows/ci.yml). Every <interval> seconds it appends one line per sample to
# <out-dir>/system.csv (CPU, run queue, memory, paging, disk queue) and one line per process of
# interest to <out-dir>/processes.csv (CPU seconds, working set, private bytes, threads, and how
# many of its threads are Running, Ready, and waiting for each reason, PageIn and Executive
# included). <out-dir>/hosts.csv maps each test host's pid to its test assembly and TFM, so a
# freeze in a .trx can be lined up with what that process was doing at the same moment.
#
# Why: on windows-latest a single test host sometimes completes no test for 40 to 160 s while
# the other hosts on the same runner keep completing hundreds; every #373, #316 and #321-class
# failure is a test whose wall-clock budget ran out inside such a freeze. The .trx files show
# the freeze, not its cause; this shows whether the frozen process was starved of CPU, paging,
# or blocked.
#
# It stops when <out-dir>/stop exists. It never fails the CI step: every error is logged and
# the loop continues.
#
# Usage: load-sampler.ps1 <out-dir> [interval-seconds]
param(
    [Parameter(Mandatory = $true)][string]$OutDir,
    [int]$Interval = 2
)

$ErrorActionPreference = 'Continue'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$system = Join-Path $OutDir 'system.csv'
$processes = Join-Path $OutDir 'processes.csv'
$hosts = Join-Path $OutDir 'hosts.csv'
$errors = Join-Path $OutDir 'sampler-errors.log'
$stop = Join-Path $OutDir 'stop'

$counters = @(
    '\Processor(_Total)\% Processor Time',
    '\System\Processor Queue Length',
    '\Memory\Available MBytes',
    '\Memory\Committed Bytes',
    '\Memory\Commit Limit',
    '\Memory\Pages Input/sec',
    '\Memory\Page Faults/sec',
    '\PhysicalDisk(_Total)\Current Disk Queue Length',
    '\PhysicalDisk(_Total)\Disk Bytes/sec'
)

'utc,cpu_pct,run_queue,avail_mb,committed_mb,commit_limit_mb,pages_in_per_s,faults_per_s,disk_queue,disk_mb_per_s' |
    Set-Content -Path $system
'utc,pid,name,cpu_s,ws_mb,private_mb,threads,running,ready,wait_pagein,wait_executive,wait_user,wait_other' |
    Set-Content -Path $processes
'pid,name,started_utc,command_line' | Set-Content -Path $hosts

$known = @{}
$interesting = '^(testhost|dotnet|ShadowDuskCLI|slangc|MsMpEng|vstest)'

while (-not (Test-Path $stop)) {
    $now = [DateTime]::UtcNow.ToString('o')
    try {
        $s = (Get-Counter -Counter $counters -ErrorAction SilentlyContinue).CounterSamples
        $v = @{}
        foreach ($c in $s) { $v[$c.Path -replace '^\\\\[^\\]+', ''] = $c.CookedValue }
        $get = { param($k) $x = $v.Keys | Where-Object { $_ -like "*$k" } | Select-Object -First 1; if ($x) { $v[$x] } else { '' } }
        $line = '{0},{1:0},{2},{3:0},{4:0},{5:0},{6:0},{7:0},{8},{9:0.0}' -f $now,
            (& $get '% processor time'), (& $get 'processor queue length'), (& $get 'available mbytes'),
            ((& $get 'committed bytes') / 1MB), ((& $get 'commit limit') / 1MB),
            (& $get 'pages input/sec'), (& $get 'page faults/sec'), (& $get 'current disk queue length'),
            ((& $get 'disk bytes/sec') / 1MB)
        Add-Content -Path $system -Value $line
    }
    catch { Add-Content -Path $errors -Value "$now counters: $_" }

    try {
        $rows = foreach ($p in Get-Process | Where-Object { $_.ProcessName -match $interesting }) {
            try {
                if (-not $known.ContainsKey($p.Id) -and $p.ProcessName -match '^(testhost|dotnet)') {
                    $known[$p.Id] = $true
                    $cmd = (Get-CimInstance Win32_Process -Filter "ProcessId = $($p.Id)" -ErrorAction SilentlyContinue).CommandLine
                    $started = try { $p.StartTime.ToUniversalTime().ToString('o') } catch { '' }
                    Add-Content -Path $hosts -Value ('{0},{1},{2},"{3}"' -f $p.Id, $p.ProcessName, $started, ($cmd -replace '"', "'"))
                }
                $running = 0; $ready = 0; $pagein = 0; $exec = 0; $user = 0; $other = 0
                foreach ($t in $p.Threads) {
                    switch ([string]$t.ThreadState) {
                        'Running' { $running++ }
                        'Ready' { $ready++ }
                        'Wait' {
                            switch ([string]$t.WaitReason) {
                                'PageIn' { $pagein++ }
                                'Executive' { $exec++ }
                                'UserRequest' { $user++ }
                                default { $other++ }
                            }
                        }
                        default { $other++ }
                    }
                }
                '{0},{1},{2},{3:0.00},{4:0},{5:0},{6},{7},{8},{9},{10},{11},{12}' -f $now, $p.Id, $p.ProcessName,
                    $p.TotalProcessorTime.TotalSeconds, ($p.WorkingSet64 / 1MB), ($p.PrivateMemorySize64 / 1MB),
                    $p.Threads.Count, $running, $ready, $pagein, $exec, $user, $other
            }
            catch { }
        }
        if ($rows) { Add-Content -Path $processes -Value $rows }
    }
    catch { Add-Content -Path $errors -Value "$now processes: $_" }

    Start-Sleep -Seconds $Interval
}
Add-Content -Path $errors -Value "$([DateTime]::UtcNow.ToString('o')) sampler stopped"
