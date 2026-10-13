[CmdletBinding()]
param(
    [ValidateSet('Check', 'Install')]
    [string] $Action = 'Check',

    [string] $CacheRoot = (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'DinoRand\deps')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$BlenderVersion = '5.2.0'
$BlenderBuildHash = 'fbe6228777e7'
$BlenderArchiveUrl = 'https://download.blender.org/release/Blender5.2/blender-5.2.0-windows-x64.zip'
$BlenderArchiveSha256 = '2D184B626C001692C362291911293B6A297179D618D95E9E9192C3A80318ADC4'
$NodeVersion = '24.16.0'
$NpmVersion = '11.13.0'
$NodeArchiveUrl = 'https://nodejs.org/download/release/v24.16.0/node-v24.16.0-win-x64.zip'
$NodeArchiveSha256 = 'edaca9bd58ec8e92037dac4e877d52f6b8f430b81c18b57e264b4e2fb111cd56'
$ValidatorVersion = '2.0.0-dev.3.10'

function Assert-DisposablePath([string] $Path, [string] $Parent) {
    $resolvedPath = [IO.Path]::GetFullPath($Path)
    $resolvedParent = [IO.Path]::GetFullPath($Parent).TrimEnd('\') + '\'
    if (-not $resolvedPath.StartsWith($resolvedParent, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing cleanup outside disposable parent: $resolvedPath"
    }
}

function Test-Blender([string] $Executable) {
    if (-not (Test-Path -LiteralPath $Executable -PathType Leaf)) {
        return $false
    }
    $versionOutput = @(& $Executable --version 2>&1)
    if ($LASTEXITCODE -ne 0) {
        return $false
    }
    $joined = $versionOutput -join "`n"
    return $joined.Contains("Blender $BlenderVersion") -and $joined.Contains($BlenderBuildHash)
}

function Test-Node([string] $NodeExecutable, [string] $NpmExecutable) {
    if (-not (Test-Path -LiteralPath $NodeExecutable -PathType Leaf) -or
        -not (Test-Path -LiteralPath $NpmExecutable -PathType Leaf)) {
        return $false
    }
    $nodeActual = (& $NodeExecutable --version).Trim().TrimStart('v')
    if ($LASTEXITCODE -ne 0) { return $false }
    $npmActual = (& $NpmExecutable --version).Trim()
    if ($LASTEXITCODE -ne 0) { return $false }
    return $nodeActual -eq $NodeVersion -and $npmActual -eq $NpmVersion
}

function Get-ValidatorVersion([string] $Root, [string] $NodeExecutable) {
    $module = Join-Path $Root 'node_modules\gltf-validator'
    if (-not (Test-Path -LiteralPath (Join-Path $module 'package.json') -PathType Leaf)) {
        return $null
    }
    if (-not (Test-Path -LiteralPath $NodeExecutable -PathType Leaf)) {
        throw 'The pinned node.exe is required to verify the Khronos glTF Validator.'
    }
    $output = & $NodeExecutable -e "const v=require(process.argv[1]);process.stdout.write(v.version());" $module
    if ($LASTEXITCODE -ne 0) {
        return $null
    }
    return ($output -join '').Trim()
}

function Install-Node([string] $Target) {
    if (Test-Path -LiteralPath $Target) {
        throw "Node target exists but does not match the exact pin; refusing to overwrite: $Target"
    }
    $curl = Get-Command curl.exe -ErrorAction SilentlyContinue
    if ($null -eq $curl) {
        throw 'curl.exe is required for the portable Node.js download.'
    }

    $temporaryParent = [IO.Path]::GetTempPath()
    $work = Join-Path $temporaryParent ("dinorand-node-install-{0}" -f [Guid]::NewGuid().ToString('N'))
    Assert-DisposablePath $work $temporaryParent
    New-Item -ItemType Directory -Path $work | Out-Null
    try {
        $archive = Join-Path $work 'node.zip'
        & $curl.Source --fail --location --retry 3 --output $archive $NodeArchiveUrl
        if ($LASTEXITCODE -ne 0) {
            throw "Node.js download failed with exit code $LASTEXITCODE."
        }
        $actualHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
        if ($actualHash -ne $NodeArchiveSha256) {
            throw "Node.js SHA-256 mismatch: expected $NodeArchiveSha256, got $actualHash."
        }

        $expanded = Join-Path $work 'expanded'
        Expand-Archive -LiteralPath $archive -DestinationPath $expanded
        $source = Join-Path $expanded 'node-v24.16.0-win-x64'
        if (-not (Test-Path -LiteralPath (Join-Path $source 'node.exe') -PathType Leaf) -or
            -not (Test-Path -LiteralPath (Join-Path $source 'npm.cmd') -PathType Leaf)) {
            throw 'The verified Node.js archive did not contain its expected top-level directory.'
        }
        New-Item -ItemType Directory -Path (Split-Path -Parent $Target) -Force | Out-Null
        Move-Item -LiteralPath $source -Destination $Target
    }
    finally {
        if (Test-Path -LiteralPath $work) {
            Assert-DisposablePath $work $temporaryParent
            Remove-Item -LiteralPath $work -Recurse -Force
        }
    }
}

function Install-Blender([string] $Target) {
    if (Test-Path -LiteralPath $Target) {
        throw "Blender target exists but does not match the exact pin; refusing to overwrite: $Target"
    }
    $curl = Get-Command curl.exe -ErrorAction SilentlyContinue
    if ($null -eq $curl) {
        throw 'curl.exe is required for the portable Blender download.'
    }

    $temporaryParent = [IO.Path]::GetTempPath()
    $work = Join-Path $temporaryParent ("dinorand-blender-install-{0}" -f [Guid]::NewGuid().ToString('N'))
    Assert-DisposablePath $work $temporaryParent
    New-Item -ItemType Directory -Path $work | Out-Null
    try {
        $archive = Join-Path $work 'blender.zip'
        & $curl.Source --fail --location --retry 3 --output $archive $BlenderArchiveUrl
        if ($LASTEXITCODE -ne 0) {
            throw "Blender download failed with exit code $LASTEXITCODE."
        }
        $actualHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
        if ($actualHash -ne $BlenderArchiveSha256) {
            throw "Blender SHA-256 mismatch: expected $BlenderArchiveSha256, got $actualHash."
        }

        $expanded = Join-Path $work 'expanded'
        Expand-Archive -LiteralPath $archive -DestinationPath $expanded
        $source = Join-Path $expanded 'blender-5.2.0-windows-x64'
        if (-not (Test-Path -LiteralPath (Join-Path $source 'blender.exe') -PathType Leaf)) {
            throw 'The verified Blender archive did not contain its expected top-level directory.'
        }
        New-Item -ItemType Directory -Path (Split-Path -Parent $Target) -Force | Out-Null
        Move-Item -LiteralPath $source -Destination $Target
    }
    finally {
        if (Test-Path -LiteralPath $work) {
            Assert-DisposablePath $work $temporaryParent
            Remove-Item -LiteralPath $work -Recurse -Force
        }
    }
}

function Install-Validator([string] $Target, [string] $NpmExecutable) {
    if (Test-Path -LiteralPath $Target) {
        throw "Validator target exists but does not match the exact pin; refusing to overwrite: $Target"
    }
    if (-not (Test-Path -LiteralPath $NpmExecutable -PathType Leaf)) {
        throw 'The pinned npm.cmd is required to install the Khronos glTF Validator.'
    }

    $temporaryParent = [IO.Path]::GetTempPath()
    $work = Join-Path $temporaryParent ("dinorand-validator-install-{0}" -f [Guid]::NewGuid().ToString('N'))
    Assert-DisposablePath $work $temporaryParent
    New-Item -ItemType Directory -Path $work | Out-Null
    try {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'package.json') -Destination $work
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'package-lock.json') -Destination $work
        Push-Location $work
        try {
            & $NpmExecutable ci --ignore-scripts --no-audit --no-fund
            if ($LASTEXITCODE -ne 0) {
                throw "npm ci failed with exit code $LASTEXITCODE."
            }
        }
        finally {
            Pop-Location
        }
        New-Item -ItemType Directory -Path (Split-Path -Parent $Target) -Force | Out-Null
        Move-Item -LiteralPath $work -Destination $Target
    }
    finally {
        if (Test-Path -LiteralPath $work) {
            Assert-DisposablePath $work $temporaryParent
            Remove-Item -LiteralPath $work -Recurse -Force
        }
    }
}

try {
    if ([string]::IsNullOrWhiteSpace($CacheRoot)) {
        throw 'The user-local dependency cache root could not be resolved.'
    }
    $CacheRoot = [IO.Path]::GetFullPath($CacheRoot)
    $blenderExecutable = Join-Path $CacheRoot "blender\$BlenderVersion\blender.exe"
    $nodeRoot = Join-Path $CacheRoot "node\$NodeVersion"
    $nodeExecutable = Join-Path $nodeRoot 'node.exe'
    $npmExecutable = Join-Path $nodeRoot 'npm.cmd'
    $validatorRoot = Join-Path $CacheRoot "gltf-validator\$ValidatorVersion"

    if ($Action -eq 'Install') {
        if (-not (Test-Blender $blenderExecutable)) {
            Install-Blender (Split-Path -Parent $blenderExecutable)
        }
        if (-not (Test-Node $nodeExecutable $npmExecutable)) {
            Install-Node $nodeRoot
        }
        if ((Get-ValidatorVersion $validatorRoot $nodeExecutable) -ne $ValidatorVersion) {
            Install-Validator $validatorRoot $npmExecutable
        }
    }

    if (-not (Test-Blender $blenderExecutable)) {
        throw "Blender $BlenderVersion ($BlenderBuildHash) is not installed at $blenderExecutable. Run with -Action Install."
    }
    if (-not (Test-Node $nodeExecutable $npmExecutable)) {
        throw "Node.js $NodeVersion with npm $NpmVersion is not installed at $nodeRoot. Run with -Action Install."
    }
    $validatorActual = Get-ValidatorVersion $validatorRoot $nodeExecutable
    if ($validatorActual -ne $ValidatorVersion) {
        throw "glTF Validator $ValidatorVersion is not installed at $validatorRoot. Run with -Action Install."
    }

    [ordered]@{
        blender = [ordered]@{
            executable = $blenderExecutable
            version = $BlenderVersion
            buildHash = $BlenderBuildHash
            archiveSha256 = $BlenderArchiveSha256
        }
        node = [ordered]@{
            executable = $nodeExecutable
            version = $NodeVersion
            npm = $NpmVersion
            archiveSha256 = $NodeArchiveSha256
        }
        validator = [ordered]@{
            root = $validatorRoot
            version = $validatorActual
        }
    } | ConvertTo-Json -Depth 3
}
catch {
    Write-Error $_.Exception.Message
    exit 1
}
