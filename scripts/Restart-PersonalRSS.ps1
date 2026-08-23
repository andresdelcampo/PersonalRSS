[CmdletBinding()]
param(
    [ValidateRange(1, 65535)]
    [int]$Port = 5187,
    [ValidateRange(5, 120)]
    [int]$HealthTimeoutSeconds = 20
)

$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 5) {
    throw 'Restart-PersonalRSS.ps1 requires Windows PowerShell 5.1 or newer.'
}

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$webRoot = Join-Path $repositoryRoot 'src\PersonalRSS.Web'
$projectPath = Join-Path $webRoot 'PersonalRSS.Web.csproj'
$releaseDirectory = Join-Path $webRoot 'bin\Release\net8.0'
$executablePath = Join-Path $releaseDirectory 'PersonalRSS.Web.exe'
$baseUri = "http://127.0.0.1:$Port"
$temporaryRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$stagingDirectory = Join-Path $temporaryRoot "personalrss-publish-$([Guid]::NewGuid().ToString('N'))"
$stagingBuildDirectory = (Join-Path $stagingDirectory 'build') + [System.IO.Path]::DirectorySeparatorChar
$stagingPublishDirectory = Join-Path $stagingDirectory 'publish'
$backupDirectory = Join-Path $temporaryRoot "personalrss-backup-$([Guid]::NewGuid().ToString('N'))"
$stdoutLog = Join-Path $temporaryRoot 'personalrss-live.out.log'
$stderrLog = Join-Path $temporaryRoot 'personalrss-live.err.log'
$replacement = $null
$stoppedExistingInstance = $false

function Assert-TemporaryPath([string]$Path) {
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($temporaryRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to use a temporary path outside $temporaryRoot`: $fullPath"
    }
}

function Get-ListenerProcessId {
    $matches = @(netstat -ano | Select-String "^\s*TCP\s+127\.0\.0\.1:$Port\s+\S+\s+LISTENING\s+(\d+)\s*$")
    $ids = @($matches | ForEach-Object { [int]$_.Matches[0].Groups[1].Value } | Sort-Object -Unique)
    if ($ids.Count -gt 1) { throw "More than one process appears to listen on $baseUri`: $($ids -join ', ')" }
    if ($ids.Count -eq 1) { return $ids[0] }
    return $null
}

function Wait-ForHealth {
    $deadline = (Get-Date).AddSeconds($HealthTimeoutSeconds)
    do {
        try {
            $response = Invoke-WebRequest -Uri "$baseUri/health" -UseBasicParsing -TimeoutSec 2
            if ($response.StatusCode -eq 200) { return }
        }
        catch {
            Start-Sleep -Milliseconds 250
        }
    } while ((Get-Date) -lt $deadline)
    throw "PersonalRSS did not become healthy at $baseUri within $HealthTimeoutSeconds seconds."
}

try {
    Write-Host 'Checking outbound HTTPS before touching the live instance...'
    try {
        Invoke-WebRequest -Uri 'https://example.com/' -Method Head -UseBasicParsing -TimeoutSec 10 | Out-Null
    }
    catch {
        throw "Outbound HTTPS is unavailable in this process. Run the script from a normal network-enabled PowerShell session; do not replace the live instance from a restricted sandbox. $($_.Exception.Message)"
    }

    Assert-TemporaryPath $stagingDirectory
    Assert-TemporaryPath $stagingBuildDirectory
    Assert-TemporaryPath $stagingPublishDirectory
    Assert-TemporaryPath $backupDirectory
    New-Item -ItemType Directory -Path $stagingPublishDirectory | Out-Null
    Write-Host 'Publishing the Release build to staging...'
    & dotnet publish $projectPath --configuration Release --no-restore --output $stagingPublishDirectory "-p:OutputPath=$stagingBuildDirectory"
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

    $listenerProcessId = Get-ListenerProcessId
    if ($null -ne $listenerProcessId) {
        $listener = Get-Process -Id $listenerProcessId -ErrorAction Stop
        if ([System.IO.Path]::GetFullPath($listener.Path) -ne [System.IO.Path]::GetFullPath($executablePath)) {
            throw "Port $Port belongs to unexpected process $listenerProcessId at $($listener.Path)."
        }
        if (Test-Path -LiteralPath $releaseDirectory) {
            Copy-Item -LiteralPath $releaseDirectory -Destination $backupDirectory -Recurse
        }
        Write-Host "Stopping verified PersonalRSS PID $listenerProcessId..."
        Stop-Process -Id $listenerProcessId -Force
        $stoppedExistingInstance = $true
        $stopDeadline = (Get-Date).AddSeconds(30)
        do {
            $remainingProcess = Get-Process -Id $listenerProcessId -ErrorAction SilentlyContinue
            if ($null -eq $remainingProcess) { break }
            Start-Sleep -Milliseconds 250
        } while ((Get-Date) -lt $stopDeadline)
        if ($null -ne (Get-Process -Id $listenerProcessId -ErrorAction SilentlyContinue)) {
            throw "PersonalRSS PID $listenerProcessId did not stop within 30 seconds."
        }
    }

    New-Item -ItemType Directory -Path $releaseDirectory -Force | Out-Null
    Copy-Item -Path (Join-Path $stagingPublishDirectory '*') -Destination $releaseDirectory -Recurse -Force
    $replacement = Start-Process -FilePath $executablePath -ArgumentList '--urls', $baseUri -WorkingDirectory $webRoot -WindowStyle Hidden -RedirectStandardOutput $stdoutLog -RedirectStandardError $stderrLog -PassThru
    Wait-ForHealth

    $liveListenerProcessId = Get-ListenerProcessId
    if ($liveListenerProcessId -ne $replacement.Id) {
        throw "Healthy response did not come from replacement PID $($replacement.Id); listener PID is $liveListenerProcessId."
    }

    $feeds = @(((Invoke-WebRequest -Uri "$baseUri/api/feeds" -UseBasicParsing).Content | ConvertFrom-Json))
    Write-Host "Refreshing $($feeds.Count) configured feeds..."
    $results = @(
        foreach ($feed in $feeds) {
        try {
            $response = Invoke-RestMethod -Uri "$baseUri/api/feeds/$($feed.id)/refresh" -Method Post -TimeoutSec 45
            [pscustomobject]@{ Name = $feed.name; Ok = $true; Fetched = $response.fetched; Error = $null }
        }
        catch {
            [pscustomobject]@{ Name = $feed.name; Ok = $false; Fetched = $null; Error = $_.Exception.Message }
        }
        }
    )
    $results | Sort-Object Name | Format-Table Name, Ok, Fetched, Error -AutoSize
    $failed = @($results | Where-Object { -not $_.Ok })
    Start-Sleep -Seconds 2
    $persistedErrors = @((((Invoke-WebRequest -Uri "$baseUri/api/feeds" -UseBasicParsing).Content | ConvertFrom-Json)) | Where-Object LastError)
    $retryNames = @($failed.Name) + @($persistedErrors.Name)
    $retryFeeds = @($feeds | Where-Object { $retryNames -contains $_.Name })
    if ($retryFeeds.Count -gt 0) {
        Write-Host "Retrying $($retryFeeds.Count) transient or concurrently failed feed refreshes..."
        foreach ($feed in $retryFeeds) {
            try {
                Invoke-RestMethod -Uri "$baseUri/api/feeds/$($feed.id)/refresh" -Method Post -TimeoutSec 45 | Out-Null
            }
            catch {
                Write-Warning "$($feed.Name) retry failed: $($_.Exception.Message)"
            }
        }
        Start-Sleep -Seconds 2
    }
    $finalErrors = @((((Invoke-WebRequest -Uri "$baseUri/api/feeds" -UseBasicParsing).Content | ConvertFrom-Json)) | Where-Object LastError)
    if ($finalErrors.Count -gt 0) {
        throw "Live verification failed after retry and stabilization: $($finalErrors.Count) feeds retain lastError ($($finalErrors.Name -join ', '))."
    }

    Write-Host "PersonalRSS PID $($replacement.Id) is healthy at $baseUri; all $($feeds.Count) feeds refreshed successfully."
}
catch {
    $deploymentFailure = $_
    if ($null -ne $replacement -and -not $replacement.HasExited) {
        Stop-Process -Id $replacement.Id -Force -ErrorAction SilentlyContinue
    }
    if ($stoppedExistingInstance -and (Test-Path -LiteralPath $backupDirectory)) {
        Write-Warning 'Deployment failed after stopping the previous instance. Restoring its Release files.'
        Copy-Item -Path (Join-Path $backupDirectory '*') -Destination $releaseDirectory -Recurse -Force
        try {
            $rollback = Start-Process -FilePath $executablePath -ArgumentList '--urls', $baseUri -WorkingDirectory $webRoot -WindowStyle Hidden -RedirectStandardOutput $stdoutLog -RedirectStandardError $stderrLog -PassThru
            Wait-ForHealth
            Write-Warning "Restored the previous Release instance as PID $($rollback.Id)."
        }
        catch {
            Write-Warning "The previous files were restored, but the rollback instance did not become healthy: $($_.Exception.Message)"
        }
    }
    throw $deploymentFailure
}
finally {
    foreach ($temporaryDirectory in @($stagingDirectory, $backupDirectory)) {
        Assert-TemporaryPath $temporaryDirectory
        if (Test-Path -LiteralPath $temporaryDirectory) {
            Remove-Item -LiteralPath $temporaryDirectory -Recurse -Force
        }
    }
}
