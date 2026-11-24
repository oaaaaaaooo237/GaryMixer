$ErrorActionPreference = 'Stop'
$PSModuleAutoLoadingPreference = 'None'
$request = [Microsoft.Win32.Registry]::GetValue('HKEY_CURRENT_USER\Software\GpuPowerTray', 'Request', $null)
if ($request -isnot [string] -or $request.Length -gt 90 -or
    $request -cnotmatch '\A([a-f0-9]{32})\|(GPU-[a-fA-F0-9]{8}(?:-[a-fA-F0-9]{4}){3}-[a-fA-F0-9]{12})\|(default|[0-9]{1,5})\z') { exit 2 }

$gpu, $target = $Matches[2], $Matches[3]
$system = [Environment]::SystemDirectory
$smi = [IO.Path]::Combine($system, 'nvidia-smi.exe')
$env:Path = $system
[Environment]::CurrentDirectory = $system
$invariant = [Globalization.CultureInfo]::InvariantCulture

try {
    $limits = & $smi -i $gpu --query-gpu=power.min_limit,power.max_limit,power.default_limit --format=csv,noheader,nounits
    if ($LASTEXITCODE -ne 0 -or $limits -isnot [string]) { exit 3 }
    $values = $limits.Split(',')
    if ($values.Count -ne 3) { exit 3 }
    $minimum = [decimal]::Parse($values[0].Trim(), $invariant)
    $maximum = [decimal]::Parse($values[1].Trim(), $invariant)
    $watts = if ($target -ceq 'default') { [decimal]::Parse($values[2].Trim(), $invariant) } else { [decimal]::Parse($target, $invariant) }
    if ($watts -lt $minimum -or $watts -gt $maximum) { exit 4 }
    $null = & $smi -i $gpu -pl ($watts.ToString($invariant))
    if ($LASTEXITCODE -ne 0) { exit 5 }
    $actual = & $smi -i $gpu --query-gpu=power.limit --format=csv,noheader,nounits
    if ($LASTEXITCODE -ne 0 -or $actual -isnot [string] -or [decimal]::Parse($actual.Trim(), $invariant) -ne $watts) {
        exit 6
    }
    exit 0
} catch {
    exit 1
}
