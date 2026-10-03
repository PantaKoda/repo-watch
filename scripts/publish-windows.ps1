# Builds the portable Windows release: a self-contained win-x64 publish zipped as
#   artifacts/release/RepoWatch-<version>-win-x64.zip  (+ .sha256)
# The .NET runtime is included, so the zip runs on Windows 10 1809+ x64 without installing .NET.
#
# Reproducible: locked restore, deterministic compilation with CI path mapping, and a zip whose entries are
# sorted and stamped with the commit time. Publishing the same clean commit twice gives the same SHA-256.
#
#   pwsh scripts/publish-windows.ps1              # restore, test, publish, zip
#   pwsh scripts/publish-windows.ps1 -SkipTests   # when the tests already ran for this commit
#
# Signing is not done here: signing keys never belong in this repository (see README, "Releases").
[CmdletBinding()]
param(
    [switch]$SkipTests,
    [string]$Output = 'artifacts/release'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

function Invoke-Checked([string]$what, [scriptblock]$command) {
    Write-Host "==> $what" -ForegroundColor Cyan
    & $command
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit code $LASTEXITCODE)." }
}

$project = 'src/RepoWatch.Desktop/RepoWatch.Desktop.csproj'
$framework = 'net10.0-windows10.0.19041.0'
$runtime = 'win-x64'

$commit = (git rev-parse HEAD).Trim()
$dirty = [bool](git status --porcelain --untracked-files=no)
if ($dirty) {
    Write-Warning 'The working tree has uncommitted changes: this build is not reproducible from the commit and must not be released.'
}
# Zip timestamps come from the commit so they do not depend on when or where the build ran.
$stamp = [DateTimeOffset]::FromUnixTimeSeconds([long](git log -1 --format=%ct HEAD)).UtcDateTime

$version = (dotnet msbuild $project -getProperty:Version -p:TargetFramework=$framework).Trim()
if (-not $version) { throw 'Could not read the version from the project.' }
$name = "RepoWatch-$version-$runtime"
$publishDir = Join-Path $root "artifacts/publish/$runtime"
$outDir = Join-Path $root $Output
$zip = Join-Path $outDir "$name.zip"

Invoke-Checked 'Restore (locked)' { dotnet restore RepoWatch.slnx --locked-mode }
if (-not $SkipTests) {
    Invoke-Checked 'Build' { dotnet build RepoWatch.slnx -c Release --no-restore }
    Invoke-Checked 'Test' { dotnet test --solution RepoWatch.slnx -c Release --no-build }
}

# Start from empty output folders so files from an earlier publish cannot leak into the zip.
foreach ($dir in $publishDir, (Join-Path $root "src/RepoWatch.Desktop/obj/Release/$framework/$runtime")) {
    if (Test-Path $dir) { Remove-Item -Recurse -Force $dir }
}
Invoke-Checked 'Publish' {
    dotnet publish $project -c Release -f $framework -r $runtime --self-contained `
        -p:ContinuousIntegrationBuild=true -p:SatelliteResourceLanguages=en -o $publishDir
}
if (-not (Test-Path (Join-Path $publishDir 'RepoWatch.exe'))) { throw 'The publish did not produce RepoWatch.exe.' }

Write-Host '==> Zip' -ForegroundColor Cyan
New-Item -ItemType Directory -Force $outDir | Out-Null
if (Test-Path $zip) { Remove-Item -Force $zip }
Add-Type -AssemblyName System.IO.Compression
# Debug symbols stay in artifacts/publish for crash analysis; they are not shipped in the zip.
$files = Get-ChildItem $publishDir -Recurse -File | Where-Object { $_.Extension -ne '.pdb' } |
    ForEach-Object { [pscustomobject]@{ File = $_; Entry = 'RepoWatch/' + [IO.Path]::GetRelativePath($publishDir, $_.FullName).Replace('\', '/') } } |
    Sort-Object { $_.Entry } -Culture ([cultureinfo]::InvariantCulture) -CaseSensitive
$stream = [IO.File]::Open($zip, [IO.FileMode]::CreateNew)
try {
    $archive = New-Object IO.Compression.ZipArchive($stream, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($item in $files) {
            $entry = $archive.CreateEntry($item.Entry, [IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = $stamp
            $source = $item.File.OpenRead()
            $target = $entry.Open()
            try { $source.CopyTo($target) } finally { $target.Dispose(); $source.Dispose() }
        }
    } finally { $archive.Dispose() }
} finally { $stream.Dispose() }

$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path "$zip.sha256" -Value "$hash  $name.zip" -Encoding ascii -NoNewline
$size = [math]::Round((Get-Item $zip).Length / 1MB, 1)

Write-Host ''
Write-Host "Version : $version ($($commit.Substring(0, 7))$(if ($dirty) { ', dirty' }))"
Write-Host "Zip     : $zip ($size MB, $($files.Count) files)"
Write-Host "SHA-256 : $hash"
Write-Host "Run     : extract the zip, then start RepoWatch\RepoWatch.exe"
