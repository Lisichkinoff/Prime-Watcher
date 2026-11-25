param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = '1.0.2',
    [string]$OutputDirectory = 'artifacts'
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
Push-Location (Join-Path $PSScriptRoot '..')
try {
    $outputRoot = [System.IO.Path]::GetFullPath($OutputDirectory, (Get-Location).Path)
    $publish = Join-Path $outputRoot "publish-$Version"
    $packages = Join-Path $outputRoot "release-$Version"
    dotnet tool restore
    dotnet publish src/PrimeWatcher/PrimeWatcher.csproj -c Release -r win-x64 --self-contained true -p:Version=$Version -warnaserror --nologo -o $publish
    dotnet vpk pack --skip-updates --packId PrimeWatcher --packVersion $Version --packTitle 'Prime Watcher' --mainExe PrimeWatcher.exe --packDir $publish --outputDir $packages --runtime win10-x64 --channel win-x64 --noPortable

    $installer = @(Get-ChildItem -LiteralPath $packages -Filter '*-Setup.exe' | Where-Object Name -ne 'PrimeWatcher-Setup.exe')
    if ($installer.Count -gt 1) {
        throw 'Expected a single channel-specific Windows installer.'
    }
    if ($installer.Count -eq 1) {
        $destination = Join-Path $packages 'PrimeWatcher-Setup.exe'
        Move-Item -LiteralPath $installer[0].FullName -Destination $destination -Force
    }

    foreach ($asset in @('PrimeWatcher-Setup.exe', 'releases.win-x64.json')) {
        if (-not (Test-Path -LiteralPath (Join-Path $packages $asset))) {
            throw "Missing release asset $asset."
        }
    }
    $feed = Get-Content -LiteralPath (Join-Path $packages 'releases.win-x64.json') -Raw | ConvertFrom-Json
    $full = $feed.Assets | Where-Object { $_.Version -eq $Version -and $_.Type -eq 'Full' }
    if (@($full).Count -ne 1 -or -not (Test-Path -LiteralPath (Join-Path $packages $full.FileName))) {
        throw "Missing full Velopack package for $Version."
    }
    $packages
}
finally {
    Pop-Location
}
