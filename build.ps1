param(
    [string]$ZigPath = 'zig',
    [string]$PythonPath = 'py',
    [switch]$Test,
    [string]$SignToolPath = '',
    [string]$SigningCertificateThumbprint = '',
    [string]$TimestampUrl = 'http://timestamp.digicert.com',
    [switch]$RequireSignature
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Invoke-Checked([string]$Program, [string[]]$Arguments) {
    & $Program @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Command failed ($LASTEXITCODE): $Program" }
}

$SigningCertificateThumbprint = ($SigningCertificateThumbprint -replace '[\s-]', '')
$signingRequested = -not [string]::IsNullOrWhiteSpace($SigningCertificateThumbprint)
$signTool = $null
if ($signingRequested) {
    if ([string]::IsNullOrWhiteSpace($SignToolPath)) {
        $signTool = (Get-Command signtool.exe -ErrorAction Stop).Source
    } else {
        $signTool = (Get-Command $SignToolPath -ErrorAction Stop).Source
    }
} elseif ($RequireSignature) {
    throw 'RequireSignature requires SigningCertificateThumbprint.'
}

function Sign-Artifact([string]$Path) {
    if (-not $signingRequested) { return }
    Invoke-Checked $signTool @('sign', '/sha1', $SigningCertificateThumbprint,
        '/fd', 'SHA256', '/tr', $TimestampUrl, '/td', 'SHA256', $Path)
    Invoke-Checked $signTool @('verify', '/pa', '/all', $Path)
}

$root = $PSScriptRoot
$src = Join-Path $root 'src'
$tests = Join-Path $root 'tests'
$dist = Join-Path $root 'dist'
$single = Join-Path $dist 'single'
$build = Join-Path $root 'build'
$testOut = Join-Path $build 'tests'
$csc = Join-Path ([Environment]::GetFolderPath('Windows')) 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $csc)) { throw '64-bit .NET Framework C# compiler not found.' }
$zig = (Get-Command $ZigPath -ErrorAction Stop).Source
New-Item -ItemType Directory -Force -Path $build, $single | Out-Null
if (-not $signingRequested) {
    Write-Warning 'No signing certificate supplied; release artifacts will be unsigned and may trigger SmartScreen or behavior-based detections.'
}

Push-Location $build
try {
    $module = Join-Path $build 'ClockNativeV7.dll'
    $icon = Join-Path $build 'clock.ico'
    $iconTool = Join-Path $build 'generate_icon.exe'
    Invoke-Checked $zig @('cc', '-target', 'x86_64-windows-gnu', '-O2', '-shared',
        (Join-Path $src 'clock_hook.c'), '-o', $module)
    Sign-Artifact $module
    Invoke-Checked $csc @('/nologo','/codepage:65001','/platform:x64','/reference:System.Drawing.dll',
        ('/out:'+$iconTool),(Join-Path $root 'tools\GenerateIcon.cs'))
    Invoke-Checked $iconTool @($icon)
    $appSources = @('NativeTaskbarClock.cs','ClockSettings.cs','SettingsForm.cs','TemplateEditor.cs','TemplateParts.cs','SettingsIpc.cs','StartupRetry.cs','Autostart.cs','ClockStorage.cs') |
        ForEach-Object { Join-Path $src $_ }
    $appArgs = @('/nologo','/codepage:65001','/target:winexe','/platform:x64','/optimize+',
        ('/out:' + (Join-Path $single 'TimeZoneClock.exe')), '/reference:System.Windows.Forms.dll',
        '/reference:System.Drawing.dll', '/reference:System.Web.Extensions.dll',
        ('/win32icon:' + $icon), ('/resource:' + $module + ',ClockNativeV7.dll')) + $appSources
    Invoke-Checked $csc $appArgs
    Sign-Artifact (Join-Path $single 'TimeZoneClock.exe')
    Write-Host "Single-file build complete: $single"

    if ($Test) {
        $python = (Get-Command $PythonPath -ErrorAction Stop).Source
        New-Item -ItemType Directory -Force -Path $testOut | Out-Null
        $cscCommon = @('/nologo','/codepage:65001','/platform:x64')
        Invoke-Checked $csc ($cscCommon + @(('/out:' + (Join-Path $testOut 'retry.exe')),
            (Join-Path $src 'StartupRetry.cs'), (Join-Path $tests 'startup_retry_test.cs')))
        Invoke-Checked $csc ($cscCommon + @(('/out:' + (Join-Path $testOut 'autostart.exe')),
            (Join-Path $src 'Autostart.cs'), (Join-Path $tests 'autostart_test.cs')))
        Invoke-Checked $csc ($cscCommon + @('/reference:System.Web.Extensions.dll', ('/out:' + (Join-Path $testOut 'settings.exe')),
            (Join-Path $src 'ClockSettings.cs'), (Join-Path $src 'TemplateParts.cs'), (Join-Path $tests 'settings_test.cs')))
        Invoke-Checked $csc ($cscCommon + @('/reference:System.Windows.Forms.dll', '/reference:System.Drawing.dll',
            '/reference:System.Web.Extensions.dll', ('/out:' + (Join-Path $testOut 'settings_ui.exe')),
            (Join-Path $src 'ClockSettings.cs'), (Join-Path $src 'SettingsForm.cs'), (Join-Path $src 'TemplateEditor.cs'),
            (Join-Path $src 'TemplateParts.cs'), (Join-Path $tests 'settings_ui_test.cs')))
        Invoke-Checked $csc ($cscCommon + @(('/out:' + (Join-Path $testOut 'template_parts.exe')),
            (Join-Path $src 'TemplateParts.cs'), (Join-Path $tests 'template_parts_test.cs')))
        Invoke-Checked $csc ($cscCommon + @('/reference:System.Windows.Forms.dll', '/reference:System.Drawing.dll',
            '/reference:System.Web.Extensions.dll', ('/out:' + (Join-Path $testOut 'template_editor.exe')),
            (Join-Path $src 'ClockSettings.cs'), (Join-Path $src 'TemplateParts.cs'), (Join-Path $src 'TemplateEditor.cs'),
            (Join-Path $tests 'template_editor_test.cs')))
        Invoke-Checked $csc ($cscCommon + @('/reference:System.Web.Extensions.dll', ('/out:' + (Join-Path $testOut 'storage.exe')),
            (Join-Path $src 'ClockSettings.cs'), (Join-Path $src 'TemplateParts.cs'), (Join-Path $src 'ClockStorage.cs'), (Join-Path $src 'Autostart.cs'), (Join-Path $tests 'storage_test.cs')))
        Invoke-Checked $csc ($cscCommon + @('/reference:System.Drawing.dll', ('/out:' + (Join-Path $testOut 'package.exe')),
            (Join-Path $tests 'package_test.cs')))
        $pipeSource = Join-Path $testOut 'SettingsIpc.test.cs'
        $pipeContent = [IO.File]::ReadAllText((Join-Path $src 'SettingsIpc.cs'))
        if (-not $pipeContent.Contains('"DualZoneClockV9_"')) { throw 'Cannot isolate the test pipe name.' }
        $pipeContent = $pipeContent.Replace('"DualZoneClockV9_"', ('"DualZoneClockV9Test' + [Guid]::NewGuid().ToString('N') + '_"'))
        [IO.File]::WriteAllText($pipeSource, $pipeContent, (New-Object Text.UTF8Encoding $false))
        Invoke-Checked $csc ($cscCommon + @('/reference:System.Windows.Forms.dll',
            '/reference:System.Web.Extensions.dll', ('/out:' + (Join-Path $testOut 'ipc.exe')),
            (Join-Path $src 'ClockSettings.cs'), (Join-Path $src 'TemplateParts.cs'), $pipeSource, (Join-Path $tests 'ipc_test.cs')))
        Invoke-Checked $zig @('cc', '-target', 'x86_64-windows-gnu', '-O2', '-shared', '-DCLOCK_TEST',
            (Join-Path $src 'clock_hook.c'), '-o', (Join-Path $testOut 'clock_hook.test.dll'))
        Invoke-Checked $zig @('cc', '-target', 'x86_64-windows-gnu', '-O0',
            (Join-Path $tests 'test_clock_hook.c'), '-o', (Join-Path $testOut 'test_clock_hook.exe'))
        Invoke-Checked $zig @('cc', '-target', 'x86_64-windows-gnu', '-O0',
            (Join-Path $tests 'fixture_clock.c'), '-o', (Join-Path $testOut 'fixture_clock.exe'))
        Copy-Item -LiteralPath (Join-Path $tests 'test_remote.py') -Destination (Join-Path $testOut 'test_remote.py') -Force
        Copy-Item -LiteralPath (Join-Path $tests 'load_module_fixture.py') -Destination (Join-Path $testOut 'load_module_fixture.py') -Force
        Push-Location $testOut
        try {
            foreach ($name in @('retry.exe','autostart.exe','settings.exe','settings_ui.exe','template_parts.exe','template_editor.exe','storage.exe','ipc.exe','test_clock_hook.exe')) {
                Invoke-Checked (Join-Path $testOut $name) @()
            }
            Invoke-Checked (Join-Path $testOut 'package.exe') @((Join-Path $single 'TimeZoneClock.exe'))
            $pythonArgs = @()
            if ([IO.Path]::GetFileName($python) -ieq 'py.exe') { $pythonArgs += '-3' }
            $pythonArgs += (Join-Path $testOut 'test_remote.py')
            Invoke-Checked $python $pythonArgs
            $loadArgs = @()
            if ([IO.Path]::GetFileName($python) -ieq 'py.exe') { $loadArgs += '-3' }
            $loadArgs += @((Join-Path $testOut 'load_module_fixture.py'), $module)
            Invoke-Checked $python $loadArgs
        } finally { Pop-Location }
        Write-Host 'All isolated tests passed.'
    }
} finally { Pop-Location }
