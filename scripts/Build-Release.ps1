[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$')]
    [string]$Version,

    [string]$OutputRoot,

    [switch]$SkipInstaller
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repositoryRoot "artifacts\release\$Version"
}

function Assert-RepositoryDotnetSdk {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepositoryRoot
    )

    $globalJsonPath = Join-Path $RepositoryRoot 'global.json'
    if (-not (Test-Path -LiteralPath $globalJsonPath -PathType Leaf)) {
        throw "global.json was not found: $globalJsonPath"
    }

    $requiredSdk = ((Get-Content -LiteralPath $globalJsonPath -Raw) | ConvertFrom-Json).sdk.version
    if ([string]::IsNullOrWhiteSpace($requiredSdk)) {
        throw "Could not read .NET SDK version from $globalJsonPath"
    }

    $dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -eq $dotnetCommand) {
        throw "dotnet was not found. Install .NET SDK $requiredSdk as specified by global.json."
    }

    Push-Location $RepositoryRoot
    try {
        $actualSdk = (& dotnet --version 2>$null)
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet could not select SDK $requiredSdk. Install that exact SDK version."
        }
    }
    finally {
        Pop-Location
    }

    if ($actualSdk -ne $requiredSdk) {
        throw "Kkindle uses one .NET SDK version: $requiredSdk from global.json; found $actualSdk."
    }
}

Assert-RepositoryDotnetSdk -RepositoryRoot $repositoryRoot

$OutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
if (Test-Path -LiteralPath $OutputRoot) {
    throw "Output directory already exists: $OutputRoot"
}

$publishDirectory = Join-Path $OutputRoot "Kkindle-$Version-win-x64"
New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null

$projectPath = Join-Path $repositoryRoot 'src\Kkindle.Desktop.Windows\Kkindle.Desktop.Windows.csproj'
if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
    throw "Avalonia desktop project was not found: $projectPath"
}
$publishArguments = @(
    'publish', $projectPath,
    '-c', 'Release',
    '-p:Platform=x64',
    '-r', 'win-x64',
    '--self-contained', 'true',
    "-p:Version=$Version",
    '-o', $publishDirectory
)

Push-Location $repositoryRoot
try {
    & dotnet @publishArguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE"
    }
}
finally {
    Pop-Location
}

$applicationPath = Join-Path $publishDirectory 'Kkindle.exe'
$licensePath = Join-Path $publishDirectory 'LICENSE'
if (-not (Test-Path -LiteralPath $applicationPath -PathType Leaf)) {
    throw "Published output does not contain Kkindle.exe: $applicationPath"
}
if (-not (Test-Path -LiteralPath $licensePath -PathType Leaf)) {
    throw "Published output does not contain LICENSE: $licensePath"
}

$mcpProjectPath = Join-Path $repositoryRoot 'src\Kkindle.McpServer\Kkindle.McpServer.csproj'
if (-not (Test-Path -LiteralPath $mcpProjectPath -PathType Leaf)) {
    throw "MCP server project was not found: $mcpProjectPath"
}

$mcpPublishDirectory = Join-Path $publishDirectory 'mcp'
New-Item -ItemType Directory -Path $mcpPublishDirectory -Force | Out-Null
$mcpPublishArguments = @(
    'publish', $mcpProjectPath,
    '-c', 'Release',
    '-f', 'net10.0-windows10.0.19041.0',
    '-p:Platform=x64',
    '-r', 'win-x64',
    '--self-contained', 'true',
    "-p:Version=$Version",
    '-o', $mcpPublishDirectory
)

Push-Location $repositoryRoot
try {
    & dotnet @mcpPublishArguments
    if ($LASTEXITCODE -ne 0) {
        throw "MCP server publish failed with exit code $LASTEXITCODE"
    }
}
finally {
    Pop-Location
}

$mcpApplicationPath = Join-Path $mcpPublishDirectory 'Kkindle.McpServer.exe'
if (-not (Test-Path -LiteralPath $mcpApplicationPath -PathType Leaf)) {
    throw "Published output does not contain Kkindle.McpServer.exe: $mcpApplicationPath"
}

$portableArchive = Join-Path $OutputRoot "Kkindle-$Version-win-x64-portable.zip"
Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath $portableArchive -CompressionLevel Optimal

if (-not $SkipInstaller) {
    $compiler = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
    if ($null -eq $compiler) {
        $knownCompilerPaths = @(
            @(
                (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
                (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
                (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
            ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and (Test-Path -LiteralPath $_ -PathType Leaf) }
        )
        if ($knownCompilerPaths.Count -eq 0) {
            throw 'ISCC.exe was not found. Install Inno Setup 6 or use -SkipInstaller.'
        }
        $compilerPath = $knownCompilerPaths[0]
    }
    else {
        $compilerPath = $compiler.Source
    }

    $installerScript = Join-Path $repositoryRoot 'installer\Kkindle.iss'
    $numericVersion = (($Version -split '-', 2)[0]) + '.0'
    & $compilerPath "/DMyAppVersion=$Version" "/DMyNumericVersion=$numericVersion" "/DSourceDir=$publishDirectory" "/DOutputDir=$OutputRoot" $installerScript
    if ($LASTEXITCODE -ne 0) {
        throw "Inno Setup failed with exit code $LASTEXITCODE"
    }
}

$releaseFiles = Get-ChildItem -LiteralPath $OutputRoot -File |
    Where-Object { $_.Extension -in '.exe', '.zip' } |
    Sort-Object Name

if (-not $SkipInstaller -and -not ($releaseFiles.Name -contains "Kkindle-$Version-win-x64-setup.exe")) {
    throw 'The installer was not generated.'
}

$checksumPath = Join-Path $OutputRoot 'SHA256SUMS.txt'
$checksums = foreach ($file in $releaseFiles) {
    $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash *$($file.Name)"
}
[System.IO.File]::WriteAllLines($checksumPath, $checksums, [System.Text.UTF8Encoding]::new($false))

Write-Host "Release artifacts created at $OutputRoot"
Get-ChildItem -LiteralPath $OutputRoot -File | Select-Object Name, Length
