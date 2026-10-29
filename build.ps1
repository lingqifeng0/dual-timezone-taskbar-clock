param(
    [string]$ZigPath = 'zig',
    [string]$PythonPath = 'py',
    [switch]$Test
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Invoke-Checked([string]$Program, [string[]]$Arguments) {
    & $Program @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Command failed ($LASTEXITCODE): $Program" }
}

$root = $PSScriptRoot
$src = Join-Path $root 'src'
$tests = Join-Path $root 'tests'
$dist = Join-Path $root 'dist'
$build = Join-Path $root 'build'
$testOut = Join-Path $build 'tests'
$csc = Join-Path ([Environment]::GetFolderPath('Windows')) 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $csc)) { throw '64-bit .NET Framework C# compiler not found.' }
$zig = (Get-Command $ZigPath -ErrorAction Stop).Source
New-Item -ItemType Directory -Force -Path $build, $dist | Out-Null

Push-Location $build
try {
    Invoke-Checked $zig @('cc', '-target', 'x86_64-windows-gnu', '-O2', '-shared',
        (Join-Path $src 'clock_hook.c'), '-o', (Join-Path $dist 'ClockNativeV2.dll'))
    $appSources = @('NativeTaskbarClock.cs','ClockSettings.cs','SettingsForm.cs','SettingsIpc.cs','StartupRetry.cs','Autostart.cs') |
        ForEach-Object { Join-Path $src $_ }
    $appArgs = @('/nologo','/codepage:65001','/target:winexe','/platform:x64','/optimize+',
        ('/out:' + (Join-Path $dist 'TimeZoneClock.exe')), '/reference:System.Windows.Forms.dll',
        '/reference:System.Drawing.dll', '/reference:System.Web.Extensions.dll') + $appSources
    Invoke-Checked $csc $appArgs
    $config = Join-Path $dist 'config.json'
    if (-not (Test-Path -LiteralPath $config)) {
        Copy-Item -LiteralPath (Join-Path $root 'config.example.json') -Destination $config
    }
    Write-Host "Build complete: $dist"

    if ($Test) {
        $python = (Get-Command $PythonPath -ErrorAction Stop).Source
        New-Item -ItemType Directory -Force -Path $testOut | Out-Null
        $cscCommon = @('/nologo','/codepage:65001','/platform:x64')
        Invoke-Checked $csc ($cscCommon + @(('/out:' + (Join-Path $testOut 'retry.exe')),
            (Join-Path $src 'StartupRetry.cs'), (Join-Path $tests 'startup_retry_test.cs')))
        Invoke-Checked $csc ($cscCommon + @(('/out:' + (Join-Path $testOut 'autostart.exe')),
            (Join-Path $src 'Autostart.cs'), (Join-Path $tests 'autostart_test.cs')))
        Invoke-Checked $csc ($cscCommon + @('/reference:System.Web.Extensions.dll', ('/out:' + (Join-Path $testOut 'settings.exe')),
            (Join-Path $src 'ClockSettings.cs'), (Join-Path $tests 'settings_test.cs')))
        Invoke-Checked $csc ($cscCommon + @('/reference:System.Windows.Forms.dll', '/reference:System.Drawing.dll',
            '/reference:System.Web.Extensions.dll', ('/out:' + (Join-Path $testOut 'settings_ui.exe')),
            (Join-Path $src 'ClockSettings.cs'), (Join-Path $src 'SettingsForm.cs'), (Join-Path $tests 'settings_ui_test.cs')))
        Invoke-Checked $zig @('cc', '-target', 'x86_64-windows-gnu', '-O2', '-shared', '-DCLOCK_TEST',
            (Join-Path $src 'clock_hook.c'), '-o', (Join-Path $testOut 'clock_hook.test.dll'))
        Invoke-Checked $zig @('cc', '-target', 'x86_64-windows-gnu', '-O0',
            (Join-Path $tests 'test_clock_hook.c'), '-o', (Join-Path $testOut 'test_clock_hook.exe'))
        Invoke-Checked $zig @('cc', '-target', 'x86_64-windows-gnu', '-O0',
            (Join-Path $tests 'fixture_clock.c'), '-o', (Join-Path $testOut 'fixture_clock.exe'))
        Copy-Item -LiteralPath (Join-Path $tests 'test_remote.py') -Destination (Join-Path $testOut 'test_remote.py') -Force
        Push-Location $testOut
        try {
            foreach ($name in @('retry.exe','autostart.exe','settings.exe','settings_ui.exe','test_clock_hook.exe')) {
                Invoke-Checked (Join-Path $testOut $name) @()
            }
            $pythonArgs = @()
            if ([IO.Path]::GetFileName($python) -ieq 'py.exe') { $pythonArgs += '-3' }
            $pythonArgs += (Join-Path $testOut 'test_remote.py')
            Invoke-Checked $python $pythonArgs
        } finally { Pop-Location }
        Write-Host 'All isolated tests passed.'
    }
} finally { Pop-Location }
