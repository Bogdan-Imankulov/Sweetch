param(
    [Parameter(Mandatory = $true)][string]$Installer,
    [string]$PublishDirectory = (Join-Path $PSScriptRoot '../publish')
)
$ErrorActionPreference = 'Stop'
# Run only on the disposable CI user: uninstall also removes that user's shell registration.
if ($env:GITHUB_ACTIONS -ne 'true') { throw 'Installer smoke test must run on a disposable GitHub Actions runner.' }
$Installer = (Resolve-Path -LiteralPath $Installer).Path
$PublishDirectory = (Resolve-Path -LiteralPath $PublishDirectory).Path
$testRoot = Join-Path $env:RUNNER_TEMP ("sweetch-installer-" + [guid]::NewGuid())
$installDir = Join-Path $testRoot 'installed app'
New-Item -ItemType Directory -Path $testRoot | Out-Null
$p = Start-Process $Installer -ArgumentList "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /DIR=`"$installDir`" /LOG=`"$testRoot/install.log`"" -PassThru -Wait -WindowStyle Hidden
if ($p.ExitCode -ne 0) { throw "Installer failed: $($p.ExitCode). See $testRoot/install.log" }
foreach ($source in Get-ChildItem -LiteralPath $PublishDirectory -Recurse -File) {
    $relative = [IO.Path]::GetRelativePath($PublishDirectory, $source.FullName)
    $target = Join-Path $installDir $relative
    if (!(Test-Path -LiteralPath $target) -or (Get-FileHash -LiteralPath $target).Hash -ne (Get-FileHash -LiteralPath $source.FullName).Hash) {
        throw "Installed payload differs: $relative"
    }
}
$selfTest = Join-Path $testRoot 'self-test'
$p = Start-Process (Join-Path $installDir 'Sweetch.exe') -ArgumentList "--self-test `"$selfTest`"" -PassThru -Wait -WindowStyle Hidden
Get-Content (Join-Path $selfTest 'self-test.txt')
if ($p.ExitCode -ne 0) { throw 'Installed Sweetch self-test failed.' }
$p = Start-Process (Join-Path $installDir 'unins000.exe') -ArgumentList '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART' -PassThru -Wait -WindowStyle Hidden
if ($p.ExitCode -ne 0 -or (Test-Path -LiteralPath (Join-Path $installDir 'Sweetch.exe'))) { throw 'Uninstall failed.' }
Write-Output 'Installer verified: all payload hashes match, installed app self-test passed, uninstall passed.'
