# Step 2 Test Commands - Retry Policy

## Overview

Test the retry policy implementation. Under normal operation, you won't see retriesthey only trigger on failures.

## Prerequisites

```powershell
dotnet run
```

API available at `http://localhost:5000`

## MAPE Loop Test

### Monitor - Single Prediction

```powershell
$r1 = Invoke-RestMethod "http://localhost:5000/predict/0.7"
$r1
```

**Expected output:**

```
predictedAlert : True/False
confidence     : 0.7
observationId  : <GUID>
modelVersion   : 1
```

**Expected logs:**

```
[INF] Prediction: Value=0.7, Alert=True, Probability=0.XX, ObservationId=...
```

**Notice:** No retry warnings! Prediction succeeded on first attempt.

### Analyze - Label the Observation

```powershell
Invoke-RestMethod "http://localhost:5000/label/$($r1.observationId)?actualAlert=true" -Method POST
```

### Plan - Check Statistics

```powershell
Invoke-RestMethod "http://localhost:5000/stats"
```

### Execute - Collect Training Data

```powershell
# Generate diverse observations
$r2 = Invoke-RestMethod "http://localhost:5000/predict/0.1"
$r3 = Invoke-RestMethod "http://localhost:5000/predict/0.5"
$r4 = Invoke-RestMethod "http://localhost:5000/predict/0.9"

# Label them
Invoke-RestMethod "http://localhost:5000/label/$($r2.observationId)?actualAlert=false" -Method POST
Invoke-RestMethod "http://localhost:5000/label/$($r3.observationId)?actualAlert=false" -Method POST
Invoke-RestMethod "http://localhost:5000/label/$($r4.observationId)?actualAlert=true" -Method POST

# Retrain
Invoke-RestMethod "http://localhost:5000/retrain" -Method POST
```

**Expected:**

```json
{
  "success": true,
  "oldVersion": 1,
  "newVersion": 2,
  "trainingSamples": 4
}
```

## Understanding Retry Behavior

### Why No Retry Logs?

The system is operating normally. Retries only activate on exceptions:

- ML.NET predictions are deterministic
- No external dependencies yet
- No transient failures occurring

### Verify Retry Configuration

Check `Program.cs` for the retry policy:

```csharp
var retryPolicy = new ResiliencePipelineBuilder()
    .AddRetry(new RetryStrategyOptions
    {
        MaxRetryAttempts = 3,
        Delay = TimeSpan.FromSeconds(1),
        BackoffType = DelayBackoffType.Exponential,
        OnRetry = args =>
        {
            Log.Warning("Retry attempt {AttemptNumber} after {Delay}ms",
                args.AttemptNumber + 1, args.RetryDelay.TotalMilliseconds);
            return ValueTask.CompletedTask;
        }
    })
    .Build();
```

**Configuration:**
- **3 retry attempts** (4 total with initial)
- **Exponential backoff**: 1s  2s  4s
- **Logging**: Warnings on each retry

### When Would You See Retries?

In production scenarios with:
- Transient network failures
- Temporary file system locks
- Memory pressure spikes
- Brief service unavailability

**Example retry log output:**

```
[INF] Prediction: Value=0.7...
[WRN] Retry attempt 1 after 1000ms
[INF] Prediction: Value=0.7...
```

## Retry Timing Analysis

| Attempt | Wait Time | Cumulative |
|---------|-----------|------------|
| Initial | 0s        | 0s         |
| Retry 1 | 1s        | 1s         |
| Retry 2 | 2s        | 3s         |
| Retry 3 | 4s        | 7s         |

**Worst case:** ~7 seconds before final failure

## Quick Smoke Test

```powershell
$r1 = Invoke-RestMethod "http://localhost:5000/predict/0.7"
$r1
Invoke-RestMethod "http://localhost:5000/label/$($r1.observationId)?actualAlert=true" -Method POST
Invoke-RestMethod "http://localhost:5000/stats"
```

## Key Insight

**Problem:** Persistent failures waste time retrying.

**Example:** Corrupted model file causes all requests to retry 3 times (7s each).

**Solution:** Circuit Breaker pattern (Step 3) fails fast on persistent errors.

## Next

Proceed to `Step3-AddCircuitBreaker` to implement fail-fast behavior.
