$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $compiler)) { throw 'Windows .NET Framework 64-bit compiler is unavailable.' }
$output = Join-Path $PSScriptRoot 'GpuPowerTray.exe'
$sources = 'Program.cs', 'Gpu.cs', 'MainWindow.cs' | ForEach-Object { Join-Path $PSScriptRoot $_ }
$framework = Split-Path $compiler
$wpf = 'WindowsBase.dll', 'PresentationCore.dll', 'PresentationFramework.dll' | ForEach-Object { "/reference:$framework\WPF\$_" }
$options = @('/nologo', '/target:winexe', '/platform:x64', '/optimize+', '/codepage:65001',
    '/reference:System.Windows.Forms.dll', '/reference:System.Drawing.dll', '/reference:System.Core.dll',
    '/reference:Microsoft.CSharp.dll', "/reference:$framework\System.Xaml.dll",
    "/win32manifest:$(Join-Path $PSScriptRoot 'app.manifest')",
    "/resource:$(Join-Path $PSScriptRoot 'Broker.ps1'),Broker.ps1") + $wpf
& $compiler @options "/out:$output" "/resource:$(Join-Path $PSScriptRoot 'Mixer.xaml'),Mixer.xaml" $sources
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
$setup = Join-Path $PSScriptRoot 'GaryMixerSetup.exe'
& $compiler @options /main:Installer "/out:$setup" "/resource:$output,GaryMixer.exe" $sources (Join-Path $PSScriptRoot 'Installer.cs')
if ($LASTEXITCODE -ne 0) { throw 'Setup build failed.' }
Get-Item -LiteralPath $output, $setup | Select-Object FullName, Length
