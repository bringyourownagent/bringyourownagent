<#
.SYNOPSIS
    Builds the installable APK of the BYOA agent wallet for Android: byoa-wallet-<version>-android-arm64.apk (and -android-x64.apk with -IncludeX64).

.DESCRIPTION
    1. Builds wallet\android\Byoa.Wallet.Android, Release, for android-arm64 (and android-x64 with -IncludeX64), with the assemblies inside the APK
       (EmbedAssembliesIntoApk), so that the APK runs on its own. The build happens in the project's own bin\ and obj\ folders (git ignores them);
       each APK is copied into -OutDir under the name above.
    2. Signs it with the owner's release key, with apksigner: the keystore -Keystore (default C:\Tools\byoa-android-key\byoa-android-release.jks), the key
       -KeyAlias (default byoa-android), the password from the FILE -PasswordFile (default ...\byoa-android-release.pass in the same folder; one password for
       the store and the key). The file is handed to apksigner as file:<path>: this script never reads the password, never prints it and never puts it on a
       command line. When the file is not there, the passwords are read from the environment variables named by -StorePassEnv and -KeyPassEnv instead.
       (The build itself signs with the .NET debug key first, as any Release build without a keystore does; apksigner replaces that signature.)
       With -DebugKey (and only then) the APK is signed with the .NET debug keystore: such an APK is for TESTING ONLY, it is not a release, a
       phone will not update it from a release signed with another key, and the script says so loudly and puts "TESTING-ONLY" in its file name.
       This script never creates a release key: that key is the owner's, made once and kept safe.
    3. Verifies the signature with apksigner (apksigner verify --print-certs), prints it, and checks that the certificate's SHA-256 is the one in
       -ExpectedCertSha256 (the release certificate: the one the web wallet's assetlinks.json names, so that the App Link verifies). It then prints
       the SHA-256 of the APK and its versionCode (read from the finished APK with aapt2 when the build tools have it), and writes <apk>.sha256
       ("<sha256>  <file name>") next to it.

    Nothing is overwritten: -OutDir must not hold an APK of the same name. Needs the .NET 10 SDK with the android workload, the Android SDK
    (build-tools with apksigner; -SdkDir or the ANDROID_HOME / ANDROID_SDK_ROOT variable names it) and a JDK that the android workload finds.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File wallet\packaging\make-android-package.ps1
    The release APK, signed with the owner's release key, into wallet\.publish\android-<date-time>\ (ignored by git).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File wallet\packaging\make-android-package.ps1 -DebugKey -OutDir D:\builds\byoa-android-test
    A test APK, signed with the debug key (TESTING ONLY).
#>
[CmdletBinding()]
param(
    [string]$OutDir,
    # the owner's release keystore, the key in it, and the file that holds its password (the same for the store and the key); the password is never read here
    [string]$Keystore = 'C:\Tools\byoa-android-key\byoa-android-release.jks',
    [string]$KeyAlias = 'byoa-android',
    [string]$PasswordFile = 'C:\Tools\byoa-android-key\byoa-android-release.pass',
    # used only when the password file is not there: the NAMES of the environment variables that hold the keystore password and the key password
    [string]$StorePassEnv = 'BYOA_ANDROID_STORE_PASS',
    [string]$KeyPassEnv = 'BYOA_ANDROID_KEY_PASS',
    # the SHA-256 of the release certificate (colons or not): the signed APK must carry it
    [string]$ExpectedCertSha256 = 'C3:C8:FF:B4:C2:2B:BF:7C:40:CE:A6:EB:3D:02:25:C9:99:66:E7:59:65:29:68:13:54:EC:EE:95:8D:F1:3E:D9',
    # sign with the .NET debug key instead: for testing only
    [switch]$DebugKey,
    # also build the x86_64 APK (for an emulator or a Chromebook)
    [switch]$IncludeX64,
    [string]$SdkDir
)

$ErrorActionPreference = 'Stop'
$packaging = $PSScriptRoot
$wallet = Split-Path -Parent $packaging
$project = Join-Path $wallet 'android\Byoa.Wallet.Android\Byoa.Wallet.Android.csproj'
$projectDir = Split-Path -Parent $project
$appId = 'org.bringyourownagent.wallet'

# the version: the one of Directory.Build.props, the same as the desktop wallet's
$props = [xml](Get-Content -Raw (Join-Path $wallet 'Directory.Build.props'))
$Version = ($props.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
if (-not $Version) { throw 'No <Version> in wallet\Directory.Build.props.' }

if (-not $OutDir) { $OutDir = Join-Path $wallet ('.publish\android-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
New-Item -ItemType Directory -Force $OutDir | Out-Null

# the Android SDK: the parameter, the usual variables, then the usual places
if (-not $SdkDir) { $SdkDir = $env:ANDROID_HOME }
if (-not $SdkDir) { $SdkDir = $env:ANDROID_SDK_ROOT }
if (-not $SdkDir) {
    foreach ($candidate in @("${env:ProgramFiles(x86)}\Android\android-sdk", "$env:LOCALAPPDATA\Android\Sdk")) {
        if ($candidate -and (Test-Path $candidate)) { $SdkDir = $candidate; break }
    }
}
if (-not $SdkDir -or -not (Test-Path $SdkDir)) { throw 'The Android SDK was not found: give -SdkDir, or set ANDROID_HOME.' }
$tools = Get-ChildItem (Join-Path $SdkDir 'build-tools') -Directory | Sort-Object { [version]($_.Name -replace '[^0-9.].*$', '') } -Descending |
    Where-Object { Test-Path (Join-Path $_.FullName 'apksigner.bat') } | Select-Object -First 1
if (-not $tools) { throw "No build-tools with apksigner in $SdkDir\build-tools." }
$apksigner = Join-Path $tools.FullName 'apksigner.bat'
$aapt2 = Join-Path $tools.FullName 'aapt2.exe'

# the signing: the build signs with the .NET debug key (what a Release build does without a keystore), and apksigner signs the finished APK again with the
# release key, which replaces that signature (MSBuild asks for the key password as a second value, and apksigner reads a password file only once)
$testingOnly = $false
$storePass = $null
$keyPass = $null
if (-not $DebugKey) {
    if (-not (Test-Path $Keystore)) { throw "The release keystore $Keystore does not exist. Give -Keystore, or -DebugKey for a test APK." }
    if (-not $KeyAlias) { throw 'Give -KeyAlias, the name of the key in the keystore.' }
    # the password: handed to apksigner as file:<path> (or env:<name>); this script does not open the file, and the password is on no command line
    if (Test-Path $PasswordFile) {
        # one password file, one password: apksigner reads it once, for the store, and the key has the store's password
        $storePass = 'file:' + (Resolve-Path $PasswordFile).Path
    }
    else {
        foreach ($name in @($StorePassEnv, $KeyPassEnv)) {
            if (-not [Environment]::GetEnvironmentVariable($name)) { throw "The password file $PasswordFile is not there and the environment variable $name is empty." }
        }
        $storePass = "env:$StorePassEnv"
        $keyPass = "env:$KeyPassEnv"
    }
}
else {
    $testingOnly = $true
    Write-Host ''
    Write-Host '################################################################################' -ForegroundColor Red
    Write-Host '#  -DebugKey: this APK is signed with the .NET DEBUG key.                        #' -ForegroundColor Red
    Write-Host '#  It is FOR TESTING ONLY. Do not publish it, do not give it to anyone as the    #' -ForegroundColor Red
    Write-Host '#  wallet: a release is signed with the owner''s own key (the default).           #' -ForegroundColor Red
    Write-Host '################################################################################' -ForegroundColor Red
    Write-Host ''
}

$rids = @('android-arm64')
if ($IncludeX64) { $rids += 'android-x64' }
$results = @()
foreach ($rid in $rids) {
    $name = "byoa-wallet-$Version-$rid" + $(if ($testingOnly) { '-TESTING-ONLY' } else { '' }) + '.apk'
    $target = Join-Path $OutDir $name
    if (Test-Path $target) { throw "$target already exists: nothing is overwritten. Give another -OutDir." }

    # a build that failed at the signing step leaves an unsigned APK that the next build counts as up to date: the project file is touched (its time
    # only), so that the packaging and the signing run again every time, and the verification below is of what was just signed
    (Get-Item $project).LastWriteTime = Get-Date
    Write-Host "Building $rid (Release)..."
    $arguments = @('build', $project, '-c', 'Release', '-r', $rid, '-m:4', '-p:EmbedAssembliesIntoApk=true', '-p:AndroidPackageFormat=apk', '-p:ByoaReleaseByScript=true', "-p:AndroidSdkDirectory=$SdkDir")
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed for $rid ($LASTEXITCODE)." }

    $built = Join-Path $projectDir "bin\Release\net10.0-android\$rid\$appId-Signed.apk"
    if (-not (Test-Path $built)) { throw "The build did not make $built." }
    # the release signature: the APK is signed again, into the build's own folder (not into -OutDir), by apksigner with the owner's key
    $toVerify = $built
    if (-not $testingOnly) {
        $stage = Join-Path (Split-Path -Parent $built) 'release-signed'
        New-Item -ItemType Directory -Force $stage | Out-Null
        $toVerify = Join-Path $stage $name
        $signArguments = @('sign', '--ks', (Resolve-Path $Keystore).Path, '--ks-key-alias', $KeyAlias, '--ks-pass', $storePass)
        if ($keyPass) { $signArguments += @('--key-pass', $keyPass) }
        $signArguments += @('--out', $toVerify, $built)
        Write-Host "Signing $name with the release key..."
        & $apksigner @signArguments
        if ($LASTEXITCODE -ne 0) { throw "apksigner could not sign $name ($LASTEXITCODE)." }
    }

    # the APK is verified where it was made, and is copied into -OutDir only when the signature and the certificate are right: a bad APK never gets a
    # release name in -OutDir
    Write-Host "Verifying the signature of $name..."
    $verified = & $apksigner verify --print-certs --verbose $toVerify
    $verified | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) { throw "apksigner says the signature of $name is not valid ($LASTEXITCODE)." }
    $certSha = ''
    foreach ($line in $verified) { if ($line -match 'certificate SHA-256 digest:\s*([0-9a-fA-F]+)') { $certSha = $Matches[1].ToLowerInvariant(); break } }
    $certOk = '(not checked: debug key)'
    if (-not $testingOnly) {
        $want = ($ExpectedCertSha256 -replace '[^0-9a-fA-F]', '').ToLowerInvariant()
        if ($certSha -ne $want) { throw "The certificate of $name is $certSha, not the release certificate ${want}: do not use this APK." }
        $certOk = 'yes: the release certificate'
    }
    Copy-Item $toVerify $target

    $sha = (Get-FileHash -Algorithm SHA256 $target).Hash.ToLowerInvariant()
    "$sha  $name" | Set-Content -Encoding ASCII ($target + '.sha256')
    $versionCode = '?'
    if (Test-Path $aapt2) {
        $badging = & $aapt2 dump badging $target | Select-Object -First 1
        if ($badging -match "versionCode='(\d+)'") { $versionCode = $Matches[1] }
    }

    $results += [pscustomobject]@{ File = $target; Bytes = (Get-Item $target).Length; Sha256 = $sha; VersionCode = $versionCode; CertSha256 = $certSha; CertOk = $certOk }
}

Write-Host ''
foreach ($r in $results) {
    Write-Host "APK         $($r.File)"
    Write-Host "size        $($r.Bytes) bytes"
    Write-Host "SHA-256     $($r.Sha256)"
    Write-Host "versionCode $($r.VersionCode)"
    Write-Host "certificate $($r.CertSha256)"
    Write-Host "is it the release certificate: $($r.CertOk)"
    Write-Host ''
}
if ($testingOnly) { Write-Host 'REMINDER: signed with the debug key, FOR TESTING ONLY.' -ForegroundColor Red }
