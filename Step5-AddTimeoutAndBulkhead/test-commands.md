# Step 5 - Test Commands

## Prerequisites

```powershell
dotnet run
```

API starts on `http://localhost:5000`

## MAPE Loop Testing

### Monitor: Verify Normal Operation Performance

```powershell
# Measure prediction latency
$start = Get-Date
$r1 = Invoke-RestMethod -Uri "http://localhost:5000/predict/0.7"
$duration = (Get-Date) - $start
Write-Host "Prediction completed in $($duration.TotalMilliseconds)ms"
$r1
```

**Expected output:**

```
Prediction completed in 150ms
predictedAlert : true
confidence     : 0.85
observationId  : 3fa85f64-5717-4562-b3fc-2c963f66afa6
modelVersion   : 1
fallbackUsed   : false
```

**Interpretation:** Normal predictions complete in <1 second, well under 5-second timeout threshold.

### Analyze: Timeout and Bulkhead Configuration

**Timeout configuration:**

- Threshold: 5 seconds
- Behavior: Kill slow requests, return 504 Gateway Timeout
- Protection: Prevents thread pool exhaustion from hanging operations

**Bulkhead configuration:**

- Permit limit: 2 concurrent retrains
- Queue limit: 5 waiting requests
- Behavior: Reject request 8+ with 429 Too Many Requests
- Protection: Prevents memory overflow from concurrent ML training

### Plan: Generate Training Data

```powershell
# Generate 10 labeled observations for retraining tests
$thresholds = 0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9, 0.95
$observations = @()

foreach ($threshold in $thresholds) {
    $r = Invoke-RestMethod -Uri "http://localhost:5000/predict/$threshold"
    $actualAlert = $threshold -gt 0.5
    Invoke-WebRequest -Uri "http://localhost:5000/label/$($r.observationId)?actualAlert=$actualAlert" -Method POST | Select-Object -ExpandProperty Content | ConvertFrom-Json
    $observations += $r
}

# Verify labeled data
$stats = Invoke-RestMethod -Uri "http://localhost:5000/stats"
Write-Host "Labeled observations: $($stats.labeledCount)"
```

**Expected output:**

```json
{
  "message": "Observation labeled successfully",
  "observationId": "3fa85f64-5717-4562-b3fc-2c963f66afa6"
}
```

**Expected stats:**

```
Labeled observations: 10
```

### Execute: Trigger Retrain with Bulkhead Protection

```powershell
Invoke-WebRequest -Uri "http://localhost:5000/retrain" -Method POST | Select-Object -ExpandProperty Content | ConvertFrom-Json
```

**Expected output:**

```json
{
  "message": "Model retrained successfully",
  "previousVersion": 1,
  "newVersion": 2,
  "trainingDataCount": 10,
  "accuracy": 0.95
}
```

**Pattern Behavior:** Single retrain request executes immediately (under bulkhead limit). Concurrent requests would be queued or rejected based on bulkhead configuration.

### Monitor: Verify Model Update

```powershell
$r = Invoke-RestMethod -Uri "http://localhost:5000/predict/0.7"
$r.modelVersion
```

**Expected output:**

```
2
```

## Timeout Pattern Testing

### Normal Operation (Under Timeout)

```powershell
# Multiple fast predictions
for ($i = 1; $i -le 5; $i++) {
    $start = Get-Date
    try {
        $r = Invoke-RestMethod -Uri "http://localhost:5000/predict/0.7"
        $duration = (Get-Date) - $start
        Write-Host "Request ${i}: $([int]$duration.TotalMilliseconds)ms - Success"
    }
    catch {
        $duration = (Get-Date) - $start
        Write-Host "Request ${i}: $([int]$duration.TotalMilliseconds)ms - Failed"
    }
}
```

**Expected output:**

```
Request 1: 145ms - Success
Request 2: 152ms - Success
Request 3: 148ms - Success
Request 4: 151ms - Success
Request 5: 149ms - Success
```

**Pattern Behavior:** Timeout remains transparent during healthy operation. No performance overhead.

### Timeout Response Format

When timeout occurs (requires slow operation simulation):

**HTTP Response:**

```
Status: 504 Gateway Timeout
Body: Empty or error message
```

**Expected logs:**

```
[ERR] Request timed out after 5 seconds
[ERR] Request timed out after 5 seconds
```

**Distributed tracing tags:**

```csharp
activity?.SetTag("timeout", true);
```

## Bulkhead Pattern Testing

### Single Retrain (Within Limits)

```powershell
$start = Get-Date
$result = Invoke-WebRequest -Uri "http://localhost:5000/retrain" -Method POST | Select-Object -ExpandProperty Content | ConvertFrom-Json
$duration = (Get-Date) - $start
Write-Host "Retrain completed in $($duration.TotalSeconds)s"
$result
```

**Expected output:**

```
Retrain completed in 1.2s
{
  "message": "Model retrained successfully",
  "previousVersion": 2,
  "newVersion": 3,
  "trainingDataCount": 10,
  "accuracy": 0.95
}
```

### Bulkhead Rejection Response Format

When bulkhead is full (requires >7 concurrent requests):

**HTTP Response:**

```
Status: 429 Too Many Requests
Body: Empty or rate limit message
```

**Expected logs:**

```
[WRN] Retrain rejected - bulkhead full
[WRN] Retrain request rejected - bulkhead full (limit: 2 concurrent, 5 queued)
```

## HTTP Status Code Reference

| Exception                      | Status Code         | Meaning           | Triggered By                  |
| ------------------------------ | ------------------- | ----------------- | ----------------------------- |
| `TimeoutRejectedException`     | 504                 | Gateway Timeout   | Request exceeds 5s            |
| `BrokenCircuitException`       | 200 (with fallback) | Circuit open      | 50% failure rate              |
| `RateLimiterRejectedException` | 429                 | Too Many Requests | Bulkhead full (>7 concurrent) |
| General exception              | 200 (with fallback) | ML error          | Model failure                 |

## Quick Smoke Test

```powershell
# 1. Fast prediction (under timeout)
$r1 = Invoke-RestMethod -Uri "http://localhost:5000/predict/0.7"
Write-Host "Prediction: $($r1.predictedAlert), Fallback: $($r1.fallbackUsed)"

# 2. Label observation
Invoke-WebRequest -Uri "http://localhost:5000/label/$($r1.observationId)?actualAlert=true" -Method POST | Select-Object -ExpandProperty Content | ConvertFrom-Json

# 3. Single retrain (within bulkhead)
$r2 = Invoke-WebRequest -Uri "http://localhost:5000/retrain" -Method POST | Select-Object -ExpandProperty Content | ConvertFrom-Json
Write-Host "Model version: $($r2.previousVersion) → $($r2.newVersion)"

# 4. Check stats
Invoke-RestMethod -Uri "http://localhost:5000/stats"
```

**Expected:** All operations complete successfully within configured limits.

## Policy Composition Order Analysis

### Current Configuration: Timeout → Retry → Circuit Breaker

**Request flow:**

```
[Timeout(5s)] → [Retry(3×)] → [Circuit Breaker] → Prediction Logic
```

**Scenario 1: Fast request (1s)**

```
0s:  Request starts
0s:  Timeout starts 5s timer
0s:  Retry executes
1s:  Prediction completes
1s:  Success (no timeout, no retry)
```

**Scenario 2: Slow request (6s)**

```
0s:  Request starts
0s:  Timeout starts 5s timer
0s:  Retry executes
5s:  Timeout fires → TimeoutRejectedException
5s:  Request killed (retry never attempts)
Result: 504 Gateway Timeout after 5s
```

### Alternative: Retry → Timeout → Circuit Breaker

**Scenario 2 with different order: Slow request (6s)**

```
0s:  Request starts
0s:  Retry starts
0s:  Timeout starts 5s timer
5s:  Timeout fires
5s:  Retry catches timeout, attempts retry #1
5s:  New timeout starts
10s: Timeout fires again
10s: Retry attempts retry #2
10s: New timeout starts
15s: Timeout fires third time
Result: All retries exhausted after 15s
```

**Comparison:**

| Order                    | Max Latency | Thread Block Time | User Experience   |
| ------------------------ | ----------- | ----------------- | ----------------- |
| Timeout → Retry (Step 5) | 5s          | 5s                | Fast, predictable |
| Retry → Timeout          | 15s (3×5s)  | 15s               | Slow, frustrating |

**Production recommendation:** Timeout first for user-facing APIs.

## Observability: Metrics and Alerting

### Timeout Metrics

**Key metrics:**

- `timeout_total`: Count of timed-out requests
- `timeout_rate`: Percentage of requests timing out
- `p95_latency`: 95th percentile request duration
- `p99_latency`: 99th percentile request duration

**Alert thresholds:**

- `timeout_rate > 5%`: Investigate slow operations
- `p95_latency > 4s`: Dangerously close to timeout
- `p99_latency > 4.5s`: Most requests risking timeout

### Bulkhead Metrics

**Key metrics:**

- `bulkhead_rejected_total`: Count of rejected requests
- `bulkhead_active_count`: Current executing operations
- `bulkhead_queue_length`: Current queued requests
- `bulkhead_utilization`: active_count / permit_limit

**Alert thresholds:**

- `bulkhead_rejected_total > 10/min`: Increase permit limit or scale
- `bulkhead_queue_length > 3`: Consistent backlog forming
- `bulkhead_utilization > 0.8`: Approaching capacity

### Dashboard Queries

**Timeout timeline:**

```sql
SELECT
  DATE_TRUNC('minute', timestamp) as time,
  COUNT(*) as timeout_count,
  AVG(duration_ms) as avg_duration
FROM requests
WHERE status_code = 504
GROUP BY time
ORDER BY time DESC
```

**Bulkhead utilization:**

```sql
SELECT
  timestamp,
  active_permits,
  queued_requests,
  rejected_requests
FROM bulkhead_metrics
WHERE timestamp > NOW() - INTERVAL '1 hour'
ORDER BY timestamp DESC
```

## Production Considerations

### Timeout Tuning

| API Type                   | Recommended Timeout | Rationale                      |
| -------------------------- | ------------------- | ------------------------------ |
| **Fast lookup**            | 1-2s                | Simple queries, no computation |
| **ML prediction** (Step 5) | 5s                  | Model inference + retry buffer |
| **Report generation**      | 30s                 | Complex aggregation acceptable |
| **Batch processing**       | No timeout          | Long-running by design         |

### Bulkhead Sizing

| Operation Type           | Permit Limit | Queue Limit | Rationale                         |
| ------------------------ | ------------ | ----------- | --------------------------------- |
| **User requests**        | 100          | 50          | High throughput required          |
| **Database queries**     | 50           | 20          | Connection pool size              |
| **External API**         | 10           | 20          | Rate limit compliance             |
| **ML training** (Step 5) | 2            | 5           | Memory-intensive, low concurrency |

### When NOT to Use Bulkhead

❌ **Cheap operations:** Simple CRUD, cache reads, static files  
❌ **Already limited:** DB connection pools, OS thread pools  
❌ **High throughput required:** User authentication, logging

✅ **Use bulkhead for:** Resource-intensive ops, external dependencies, rate-limited services

## Success Criteria

- ✅ Predictions complete in <1s (well under timeout)
- ✅ Timeout kills requests after 5s (returns 504)
- ✅ Bulkhead limits concurrent retrains to 2
- ✅ Bulkhead queues 3-7th requests
- ✅ Bulkhead rejects 8+ requests with 429
- ✅ All policies log state transitions
- ✅ Distributed tracing tags present

## Next Step

Step 6 provides the complete reference implementation showing all resilience patterns integrated:

- Retry (transient failure recovery)
- Circuit Breaker (persistent failure detection)
- Fallback (graceful degradation)
- Timeout (thread protection)
- Bulkhead (resource isolation)

Complete resilience architecture ready for production deployment.
