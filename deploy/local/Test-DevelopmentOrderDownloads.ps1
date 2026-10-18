$ErrorActionPreference = 'Stop'
$runId = [Guid]::NewGuid().ToString('D')
$endpoint = 'https://localhost:7100/api/v1/gateway/order-plans'
$composeJson = $null; $compose = $null; $secret = $null; $token = $null; $authorization = $null
try {
    $composeJson = docker compose -f docker-compose.identity.yml config --format json
    if ($LASTEXITCODE -ne 0) { throw 'Local identity configuration unavailable.' }
    $compose = $composeJson | ConvertFrom-Json
    $secret = $compose.services.keycloak.environment.GATEWAY_CLIENT_SECRET
    if ([string]::IsNullOrWhiteSpace($secret)) { throw 'Gateway credential unavailable.' }
    $token = Invoke-RestMethod -Method Post -Uri 'https://localhost:8443/realms/laundry-development/protocol/openid-connect/token' `
        -ContentType 'application/x-www-form-urlencoded' -Body @{
            grant_type = 'client_credentials'; client_id = 'gateway-development'; client_secret = $secret
        } -TimeoutSec 10
    $authorization = @{ Authorization = "Bearer $($token.access_token)" }
    $missingStatus = 0
    try { $missing = Invoke-WebRequest -Uri $endpoint -UseBasicParsing -TimeoutSec 10; $missingStatus = [int]$missing.StatusCode }
    catch { if ($null -ne $_.Exception.Response) { $missingStatus = [int]$_.Exception.Response.StatusCode } }
    if ($missingStatus -ne 401) { throw 'Missing-token rejection failed.' }
    $cursor = $null; $count = 0; $pages = 0; $more = $true
    $allowed = @('schemaVersion','tenantId','plantId','orderId','orderNumber','customer','plannedReceivingDate','plannedDeliveryDate','status','notesVisibleToOperators','notes','version')
    while ($more) {
        if (++$pages -gt 100) { throw 'Smoke-check batch bound exceeded.' }
        $uri = $endpoint + '?limit=1'
        if ($null -ne $cursor) { $uri += '&cursor=' + [Uri]::EscapeDataString($cursor) }
        $response = Invoke-WebRequest -Uri $uri -Headers $authorization -UseBasicParsing -TimeoutSec 10
        if ([int]$response.StatusCode -ne 200 -or 'no-store' -notin @($response.Headers['Cache-Control'])) { throw 'Download failed.' }
        $page = $response.Content | ConvertFrom-Json
        if ([string]::IsNullOrWhiteSpace($page.nextCursor) -or @($page.items).Count -gt 1 -or
            $page.hasMore -isnot [bool] -or ($page.hasMore -and @($page.items).Count -eq 0)) { throw 'Invalid batch.' }
        foreach ($item in $page.items) {
            if ($item.tenantId -cne '11111111-1111-4111-8111-111111111111' -or
                $item.plantId -cne '22222222-2222-4222-8222-222222222222' -or
                @($item.PSObject.Properties.Name).Count -ne $allowed.Count -or
                @($item.PSObject.Properties.Name | Where-Object { $_ -notin $allowed }).Count -ne 0 -or
                ($item.notesVisibleToOperators -eq $false -and $null -ne $item.notes)) { throw 'Publication scope/privacy mismatch.' }
        }
        $retry = Invoke-RestMethod -Uri $uri -Headers $authorization -TimeoutSec 10
        if ((ConvertTo-Json -InputObject @($retry.items) -Depth 8 -Compress) -cne
            (ConvertTo-Json -InputObject @($page.items) -Depth 8 -Compress) -or $retry.hasMore -ne $page.hasMore) {
            throw 'Copies changed during the read-only retry check; retry with no concurrent edits.'
        }
        $count += @($page.items).Count
        $cursor = $page.nextCursor; $more = $page.hasMore
    }
    $caughtUp = Invoke-RestMethod -Uri ($endpoint + '?cursor=' + [Uri]::EscapeDataString($cursor)) -Headers $authorization -TimeoutSec 10
    if (@($caughtUp.items).Count -ne 0 -or $caughtUp.hasMore) { throw 'Caught-up read changed; retry with no concurrent edits.' }
    Write-Host "Order download smoke check $runId passed: $count copies across $pages bounded pages; retries preserved content; caught-up poll empty; missing token rejected."
    Write-Host 'No payloads, notes, credentials or bookmarks were printed. No gateway cache or delivery acknowledgement was written.'
} catch {
    throw "Order download smoke check $runId stopped. Check local identity, download permission and API availability; no credentials or error body were printed."
} finally {
    $composeJson = $null; $compose = $null; $secret = $null; $token = $null; $authorization = $null
}
