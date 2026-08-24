$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$publish = Join-Path $root 'FluentFlyoutWPF\bin\Release\Publish'
$out = Join-Path $root 'installer'
$payload = Join-Path $out 'payload'
$sed = Join-Path $out 'PulseFlyout-Setup.sed'
$setup = Join-Path $out 'PulseFlyout-2.14.0-Setup.exe'

if (-not (Test-Path (Join-Path $publish 'FluentFlyout.exe'))) { throw "Publish output not found: $publish" }
New-Item -ItemType Directory -Force -Path $out | Out-Null
if (Test-Path $payload) { Remove-Item -LiteralPath $payload -Recurse -Force }
New-Item -ItemType Directory -Force -Path $payload | Out-Null
Get-ChildItem -LiteralPath $publish -Force | Copy-Item -Destination $payload -Recurse -Force

$install = @'
$ErrorActionPreference = "Stop"
$installDir = Join-Path $env:ProgramFiles "PulseFlyout"
New-Item -ItemType Directory -Force -Path $installDir | Out-Null
Get-ChildItem -LiteralPath $PSScriptRoot -Force | Where-Object Name -ne "install.ps1" | Copy-Item -Destination $installDir -Recurse -Force
$shell = New-Object -ComObject WScript.Shell
$start = Join-Path $env:ProgramData "Microsoft\Windows\Start Menu\Programs\PulseFlyout.lnk"
$shortcut = $shell.CreateShortcut($start)
$shortcut.TargetPath = Join-Path $installDir "FluentFlyout.exe"
$shortcut.WorkingDirectory = $installDir
$shortcut.IconLocation = "$($shortcut.TargetPath),0"
$shortcut.Save()
$uninstall = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PulseFlyout"
New-Item -Path $uninstall -Force | Out-Null
New-ItemProperty -Path $uninstall -Name DisplayName -Value "PulseFlyout" -PropertyType String -Force | Out-Null
New-ItemProperty -Path $uninstall -Name DisplayVersion -Value "2.14.0" -PropertyType String -Force | Out-Null
New-ItemProperty -Path $uninstall -Name InstallLocation -Value $installDir -PropertyType String -Force | Out-Null
New-ItemProperty -Path $uninstall -Name UninstallString -Value "powershell.exe -NoProfile -ExecutionPolicy Bypass -Command \\"Remove-Item -LiteralPath '$installDir' -Recurse -Force; Remove-Item -LiteralPath '$start' -Force; Remove-Item -LiteralPath '$uninstall' -Recurse -Force\\"" -PropertyType String -Force | Out-Null
Start-Process -FilePath (Join-Path $installDir "FluentFlyout.exe") -WorkingDirectory $installDir
'@
Set-Content -LiteralPath (Join-Path $payload 'install.ps1') -Value $install -Encoding UTF8

$files = Get-ChildItem -LiteralPath $payload -Recurse -File
$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add('[Version]'); $lines.Add('Class=IEXPRESS'); $lines.Add('SEDVersion=3')
$lines.Add('[Options]'); $lines.Add('PackagePurpose=InstallApp'); $lines.Add('ShowInstallProgramWindow=0'); $lines.Add('HideExtractAnimation=1'); $lines.Add('RebootMode=I'); $lines.Add("TargetName=$setup"); $lines.Add('FriendlyName=PulseFlyout Setup'); $lines.Add('AppLaunched=powershell.exe -NoProfile -ExecutionPolicy Bypass -File install.ps1'); $lines.Add('SourceFiles=SourceFiles')
$lines.Add('[SourceFiles]'); $lines.Add("SourceFiles0=$payload")
$lines.Add('[SourceFiles0]')
$i = 0
foreach ($file in $files) { $lines.Add("File$i=$($file.FullName)"); $i++ }
Set-Content -LiteralPath $sed -Value $lines -Encoding ASCII
if (Test-Path $setup) { Remove-Item -LiteralPath $setup -Force }
& iexpress.exe /N /Q $sed
if ($LASTEXITCODE -eq 0 -and (Test-Path $setup)) {
    Write-Host "Created: $setup"
} else {
    $zip = Join-Path $out 'PulseFlyout-2.14.0-Setup.zip'
    if (Test-Path $zip) { Remove-Item -LiteralPath $zip -Force }
    Compress-Archive -Path (Join-Path $payload '*') -DestinationPath $zip -CompressionLevel Optimal
    Write-Warning 'IExpress could not create an EXE in this environment; created an installable ZIP instead.'
    Write-Host "Created: $zip"
}
