$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http

$environment = @{}
Get-Content (Join-Path $PSScriptRoot '..\.env') | ForEach-Object {
    if ($_ -match '^\s*([^#=]+)=(.*)$') {
        $environment[$matches[1]] = $matches[2]
    }
}
$apiBase = "http://localhost:$($environment['VIDEO_API_HOST_PORT'])"
$managementBase = "http://localhost:$($environment['RABBITMQ_MANAGEMENT_HOST_PORT'])"
$basicToken = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes(
    "$($environment['RABBITMQ_DEFAULT_USER']):$($environment['RABBITMQ_DEFAULT_PASS'])"))
$managementHeaders = @{ Authorization = "Basic $basicToken" }
$http = [System.Net.Http.HttpClient]::new()
$samplePath = Join-Path $env:TEMP 'video-processing-e2e.mp4'
$invalidPath = Join-Path $env:TEMP 'video-processing-invalid.mp4'
$correlationId = [Guid]::NewGuid().ToString('N')

function Invoke-VideoUpload([string] $path, [string] $correlation) {
    $form = [System.Net.Http.MultipartFormDataContent]::new()
    $stream = [System.IO.File]::OpenRead($path)
    $part = [System.Net.Http.StreamContent]::new($stream)
    $part.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::new('video/mp4')
    $form.Add($part, 'file', [System.IO.Path]::GetFileName($path))
    $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Post, "$apiBase/videos")
    $request.Headers.Add('X-Correlation-ID', $correlation)
    $request.Content = $form
    $response = $http.SendAsync($request).GetAwaiter().GetResult()
    $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    $request.Dispose()
    if ([int]$response.StatusCode -ne 201) {
        throw "Upload failed: HTTP $([int]$response.StatusCode) $body"
    }
    return ($body | ConvertFrom-Json)
}

function New-VideoJob([string] $videoId, [string] $type, [string] $correlation) {
    $body = @{ jobType = $type } | ConvertTo-Json -Compress
    return Invoke-RestMethod -Uri "$apiBase/videos/$videoId/jobs" -Method Post `
        -Headers @{ 'X-Correlation-ID' = $correlation } -ContentType 'application/json' -Body $body
}

function Wait-ForJob([string] $jobId, [int] $timeoutSeconds = 90) {
    $deadline = [DateTime]::UtcNow.AddSeconds($timeoutSeconds)
    do {
        $state = Invoke-RestMethod -Uri "$apiBase/jobs/$jobId"
        if ($state.Status -in @('Completed', 'Failed')) {
            return $state
        }
        Start-Sleep -Milliseconds 500
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Timed out waiting for job $jobId. Last state: $($state | ConvertTo-Json -Compress)"
}

function Get-QueueState([string] $queueName) {
    $encodedQueue = [Uri]::EscapeDataString($queueName)
    return Invoke-RestMethod -Uri "$managementBase/api/queues/%2F/$encodedQueue" -Headers $managementHeaders
}

try {
    $workerId = (& docker compose ps -q video-worker).Trim()
    if ([string]::IsNullOrWhiteSpace($workerId)) {
        throw 'video-worker container is not running.'
    }
    & docker compose exec -T video-worker ffmpeg -hide_banner -loglevel error -y `
        -f lavfi -i testsrc=size=320x240:rate=10 -t 2 -pix_fmt yuv420p -c:v mpeg4 /tmp/video-e2e.mp4
    if ($LASTEXITCODE -ne 0) { throw 'Could not generate the MP4 test asset.' }
    & docker cp "${workerId}:/tmp/video-e2e.mp4" $samplePath
    if ($LASTEXITCODE -ne 0) { throw 'Could not copy the MP4 test asset from the Worker container.' }

    $upload = Invoke-VideoUpload $samplePath $correlationId
    $thumbnail = New-VideoJob $upload.videoId 'Thumbnail' $correlationId
    $metadata = New-VideoJob $upload.videoId 'Metadata' $correlationId
    $thumbnailState = Wait-ForJob $thumbnail.jobId
    $metadataState = Wait-ForJob $metadata.jobId
    foreach ($state in @($thumbnailState, $metadataState)) {
        if ($state.Status -ne 'Completed' -or $state.Progress -ne 100 -or [string]::IsNullOrWhiteSpace($state.OutputPath)) {
            throw "Job did not complete with a MinIO result: $($state | ConvertTo-Json -Compress)"
        }
        $result = Invoke-WebRequest -Uri "$apiBase/jobs/$($state.Id)/result" -UseBasicParsing
        if ($result.StatusCode -ne 200 -or $result.RawContentLength -le 0) {
            throw "Could not download the result for job $($state.Id)."
        }
        "CORE_JOB_PASS type=$($state.JobType) status=$($state.Status) progress=$($state.Progress) output=$($state.OutputPath) resultHttp=$($result.StatusCode)"
    }

    $video = Invoke-RestMethod -Uri "$apiBase/videos/$($upload.videoId)"
    $completedVideos = Invoke-RestMethod -Uri "$apiBase/videos?status=Completed"
    if ($video.Jobs.Count -lt 2 -or $video.Status -ne 'Completed' -or
        -not (@($completedVideos | ForEach-Object Id) -contains $upload.videoId)) {
        throw 'Video detail or Completed status filtering did not include both completed jobs.'
    }
    "API_FILTER_PASS videoId=$($upload.videoId) jobs=$($video.Jobs.Count) status=$($video.Status)"

    & docker compose pause video-worker
    if ($LASTEXITCODE -ne 0) { throw 'Could not pause video-worker.' }
    try {
        $waiting = New-VideoJob $upload.videoId 'Metadata' $correlationId
        $brokerHeldMessages = 0
        for ($attempt = 0; $attempt -lt 12; $attempt++) {
            $waitingState = Invoke-RestMethod -Uri "$apiBase/jobs/$($waiting.jobId)"
            $mainQueue = Get-QueueState 'video-uploaded'
            $brokerHeldMessages = $mainQueue.messages_ready + $mainQueue.messages_unacknowledged
            if ($waitingState.Status -eq 'Pending' -and $brokerHeldMessages -gt 0) { break }
            Start-Sleep -Seconds 1
        }
        $apiHealth = Invoke-RestMethod -Uri "$apiBase/health"
        if ($waitingState.Status -ne 'Pending' -or $brokerHeldMessages -lt 1 -or $apiHealth.status -ne 'Healthy') {
            throw "Paused-worker check failed: job=$($waitingState.Status), ready=$($mainQueue.messages_ready), unacknowledged=$($mainQueue.messages_unacknowledged), health=$($apiHealth.status)"
        }
        "PAUSED_WORKER_PASS apiHealth=$($apiHealth.status) job=$($waitingState.Status) ready=$($mainQueue.messages_ready) unacknowledged=$($mainQueue.messages_unacknowledged)"
    }
    finally {
        & docker compose unpause video-worker | Out-Null
    }
    $resumedState = Wait-ForJob $waiting.jobId
    if ($resumedState.Status -ne 'Completed' -or $resumedState.Progress -ne 100) {
        throw "Worker did not resume the queued job: $($resumedState | ConvertTo-Json -Compress)"
    }
    "WORKER_RESUME_PASS status=$($resumedState.Status) progress=$($resumedState.Progress)"

    [System.IO.File]::WriteAllText($invalidPath, 'not a video stream')
    $poisonUpload = Invoke-VideoUpload $invalidPath $correlationId
    $poisonJob = New-VideoJob $poisonUpload.videoId 'Thumbnail' $correlationId
    $failedState = Wait-ForJob $poisonJob.jobId 120
    $deadQueue = Get-QueueState 'video-uploaded.dead'
    if ($failedState.Status -ne 'Failed' -or [string]::IsNullOrWhiteSpace($failedState.ErrorMessage) -or $deadQueue.messages -lt 1) {
        throw "DLQ check failed: job=$($failedState.Status), error=$($failedState.ErrorMessage), deadMessages=$($deadQueue.messages)"
    }
    "DLQ_PASS status=$($failedState.Status) errorMessagePresent=$([bool]$failedState.ErrorMessage) deadMessages=$($deadQueue.messages)"

    $serviceLogs = (& docker compose logs --since 15m video-api video-worker) -join "`n"
    if (-not $serviceLogs.Contains($correlationId)) {
        throw "Correlation ID $correlationId was not found in API/Worker logs."
    }
    "CORRELATION_PASS id=$correlationId"
    'E2E_VALIDATION_PASS'
}
finally {
    $http.Dispose()
    Remove-Item $samplePath, $invalidPath -ErrorAction SilentlyContinue
}
