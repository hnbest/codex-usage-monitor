param(
    [switch]$CopyToDesktop
)

$ErrorActionPreference = 'Stop'
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) {
    throw "csc.exe not found at $csc"
}
$wpfDir = Join-Path (Split-Path $csc) 'WPF'

$srcDir = $PSScriptRoot
$distDir = Join-Path $srcDir 'dist'
$versionPath = Join-Path $srcDir 'version.json'
$manifestPath = Join-Path $srcDir 'app.manifest'

$sources = @(
    (Join-Path $srcDir 'Models.cs'),
    (Join-Path $srcDir 'CodexApi.cs'),
    (Join-Path $srcDir 'Renderer.cs'),
    (Join-Path $srcDir 'TaskbarWidgetForm.cs'),
    (Join-Path $srcDir 'SettingsForm.cs'),
    (Join-Path $srcDir 'MainForm.cs'),
    (Join-Path $srcDir 'Program.cs')
)

$iconPath = Join-Path $srcDir 'app.ico'
$iconArgs = @()
if (Test-Path $iconPath) {
    $iconArgs = @("/win32icon:$iconPath")
}

function Get-TextHash([string]$text) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $bytes = $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($text))
        return [BitConverter]::ToString($bytes).Replace('-', '')
    }
    finally {
        $sha.Dispose()
    }
}

function Get-BuildInputHash {
    $inputs = $sources + $PSCommandPath + $manifestPath
    if (Test-Path -LiteralPath $iconPath) {
        $inputs += $iconPath
    }
    $hashes = foreach ($path in $inputs) {
        '{0}:{1}' -f [IO.Path]::GetFileName($path), (Get-FileHash -LiteralPath $path).Hash
    }
    return Get-TextHash ($hashes -join "`n")
}

$mutexName = 'Local\CodexUsageMonitorBuild-' + (Get-TextHash $srcDir.ToUpperInvariant())
$buildMutex = [Threading.Mutex]::new($false, $mutexName)
$locked = $false
try {
    try {
        $locked = $buildMutex.WaitOne()
    }
    catch [Threading.AbandonedMutexException] {
        $locked = $true
    }

    $state = Get-Content -LiteralPath $versionPath -Raw | ConvertFrom-Json
    $inputHash = Get-BuildInputHash
    $version = [version]$state.version
    if ($state.inputHash -and $state.inputHash -ne $inputHash) {
        $version = [version]::new($version.Major, $version.Minor, ($version.Build + 1))
    }

    $fileName = "CodexUsageMonitor-$version.exe"
    $outExe = Join-Path $distDir $fileName
    if ($state.inputHash -ne $inputHash -or -not (Test-Path -LiteralPath $outExe)) {
        $buildId = [Guid]::NewGuid().ToString('N')
        $stageDir = Join-Path $distDir ".build-$buildId"
        $stageExe = Join-Path $stageDir $fileName
        $assemblyInfo = Join-Path $stageDir 'AssemblyInfo.cs'
        $stageState = Join-Path $srcDir ".version-$buildId.tmp"
        New-Item -ItemType Directory -Force -Path $stageDir | Out-Null
        try {
            @"
using System.Reflection;
[assembly: AssemblyProduct("Codex Usage Monitor")]
[assembly: AssemblyVersion("$version")]
[assembly: AssemblyFileVersion("$version")]
[assembly: AssemblyInformationalVersion("$version")]
"@ | Set-Content -LiteralPath $assemblyInfo -Encoding ASCII

            & $csc /nologo /target:winexe /optimize+ /utf8output `
                $iconArgs `
                "/win32manifest:$manifestPath" `
                /r:System.dll `
                /r:System.Core.dll `
                /r:System.Drawing.dll `
                /r:System.Windows.Forms.dll `
                /r:System.Web.Extensions.dll `
                "/r:$wpfDir\UIAutomationClient.dll" `
                "/r:$wpfDir\UIAutomationTypes.dll" `
                "/r:$wpfDir\WindowsBase.dll" `
                /out:$stageExe `
                $sources $assemblyInfo

            if ($LASTEXITCODE -ne 0) {
                throw "Build failed with exit code $LASTEXITCODE"
            }
            if ((Get-BuildInputHash) -ne $inputHash) {
                throw 'Build inputs changed during compilation. Run the build again.'
            }
            if ($state.inputHash -ne $inputHash) {
                [ordered]@{ version = $version.ToString(); inputHash = $inputHash } |
                    ConvertTo-Json | Set-Content -LiteralPath $stageState -Encoding ASCII
                [IO.File]::Replace($stageState, $versionPath, [NullString]::Value)
            }

            [IO.File]::Move($stageExe, $outExe)
        }
        finally {
            foreach ($path in @($stageExe, $assemblyInfo, $stageState, $stageDir)) {
                if (Test-Path -LiteralPath $path) {
                    Remove-Item -LiteralPath $path
                }
            }
        }
    }

    $fi = Get-Item -LiteralPath $outExe
    Write-Output ("Package: {0} ({1:N1} KB)" -f $fi.FullName, ($fi.Length / 1KB))

    if ($CopyToDesktop) {
        $desktop = [Environment]::GetFolderPath('Desktop')
        $dest = Join-Path $desktop $fileName
        Copy-Item -LiteralPath $outExe -Destination $dest -Force
        Write-Output ("Copied to Desktop: {0}" -f $dest)
    }
}
finally {
    if ($locked) {
        $buildMutex.ReleaseMutex()
    }
    $buildMutex.Dispose()
}
