$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$publish = Join-Path $root 'FluentFlyoutWPF\bin\Release\Publish'
$stage = Join-Path $root 'installer\stage'
$out = Join-Path $root 'installer'
$kit = 'C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64'
$makeAppx = Join-Path $kit 'makeappx.exe'
$signTool = Join-Path $kit 'signtool.exe'
$version = '2.15.0.0'
$packageName = "PulseFlyout-$version-x64.msix"
$pfx = Join-Path $out 'PulseFlyout-Dev.pfx'
$cer = Join-Path $out 'PulseFlyout-Dev.cer'

if (-not (Test-Path (Join-Path $publish 'FluentFlyout.exe'))) {
    throw "Publish output not found: $publish"
}
if (-not (Test-Path $makeAppx) -or -not (Test-Path $signTool)) {
    throw 'Windows SDK makeappx.exe/signtool.exe not found.'
}

New-Item -ItemType Directory -Force -Path $out | Out-Null
if (Test-Path $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null
Copy-Item -LiteralPath (Join-Path $publish '*') -Destination $stage -Recurse -Force

$images = Join-Path $stage 'Images'
New-Item -ItemType Directory -Force -Path $images | Out-Null
$sourceImages = Join-Path $root 'FluentFlyoutMSIX\Images'
$imageMap = @{
    'StoreLogo.png' = 'StoreLogo.scale-100.png'
    'Square150x150Logo.png' = 'Square150x150Logo.scale-100.png'
    'FluentFlyout.png' = 'FluentFlyout.targetsize-48.png'
    'Wide310x150Logo.png' = 'Wide310x150Logo.scale-100.png'
    'SmallTile.png' = 'SmallTile.scale-100.png'
    'LargeTile.png' = 'LargeTile.scale-100.png'
    'SplashScreen.png' = 'SplashScreen.scale-100.png'
}
foreach ($entry in $imageMap.GetEnumerator()) {
    Copy-Item -LiteralPath (Join-Path $sourceImages $entry.Value) -Destination (Join-Path $images $entry.Key) -Force
}

$manifest = @"
<?xml version=""1.0"" encoding=""utf-8""?>
<Package xmlns=""http://schemas.microsoft.com/appx/manifest/foundation/windows10"" xmlns:uap=""http://schemas.microsoft.com/appx/manifest/uap/windows10"" xmlns:rescap=""http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"" xmlns:desktop=""http://schemas.microsoft.com/appx/manifest/desktop/windows10"" IgnorableNamespaces=""uap rescap desktop"">
  <Identity Name=""PulseFlyout"" Publisher=""CN=PulseFlyout"" Version=""$version"" />
  <Properties>
    <DisplayName>PulseFlyout</DisplayName>
    <PublisherDisplayName>PulseFlyout Contributors</PublisherDisplayName>
    <Logo>Images\StoreLogo.png</Logo>
  </Properties>
  <Dependencies>
    <TargetDeviceFamily Name=""Windows.Desktop"" MinVersion=""10.0.19041.0"" MaxVersionTested=""10.0.26100.0"" />
  </Dependencies>
  <Resources><Resource Language=""en-US"" /></Resources>
  <Applications>
    <Application Id=""App"" Executable=""FluentFlyout.exe"" EntryPoint=""Windows.FullTrustApplication""><uap:VisualElements AppListEntry=""none"" DisplayName=""PulseFlyout"" Description=""PulseFlyout audio and media flyout for Windows"" BackgroundColor=""transparent"" Square150x150Logo=""Images\Square150x150Logo.png"" Square44x44Logo=""Images\FluentFlyout.png""><uap:DefaultTile Wide310x150Logo=""Images\Wide310x150Logo.png"" ShortName=""PulseFlyout"" Square71x71Logo=""Images\SmallTile.png"" Square310x310Logo=""Images\LargeTile.png"" /><uap:SplashScreen Image=""Images\SplashScreen.png"" /></uap:VisualElements><Extensions><desktop:Extension Category=""windows.startupTask""><desktop:StartupTask TaskId=""PulseFlyoutStartup"" Enabled=""false"" DisplayName=""PulseFlyout"" /></desktop:Extension></Extensions></Application>
  </Applications>
  <Capabilities><Capability Name=""internetClient"" /><rescap:Capability Name=""runFullTrust"" /></Capabilities>
</Package>
"@
Set-Content -LiteralPath (Join-Path $stage 'AppxManifest.xml') -Value $manifest -Encoding UTF8

if (-not (Test-Path $pfx)) {
    $cert = New-SelfSignedCertificate -Type Custom -Subject 'CN=PulseFlyout' -FriendlyName 'PulseFlyout development package' -KeyUsage DigitalSignature -CertStoreLocation 'Cert:\CurrentUser\My'
    Export-Certificate -Cert $cert -FilePath $cer | Out-Null
    Export-PfxCertificate -Cert $cert -FilePath $pfx -Password (ConvertTo-SecureString -String '' -AsPlainText -Force) | Out-Null
}
if (-not (Test-Path $cer)) {
    $cert = Get-PfxCertificate -FilePath $pfx
    Export-Certificate -Cert $cert -FilePath $cer | Out-Null
}

$msix = Join-Path $out $packageName
if (Test-Path $msix) { Remove-Item -LiteralPath $msix -Force }
& $makeAppx pack /d $stage /p $msix /o
if ($LASTEXITCODE -ne 0) { throw "makeappx failed with exit code $LASTEXITCODE" }
& $signTool sign /fd SHA256 /f $pfx /p '' $msix
if ($LASTEXITCODE -ne 0) { throw "signtool failed with exit code $LASTEXITCODE" }

Write-Host "Created: $msix"
Write-Host "Certificate: $cer"
