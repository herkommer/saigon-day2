# Step 2 - Add Retry Policy

## Overview

Implement Polly''s retry pattern to handle transient failures in the prediction endpoint.

**Challenge:** ML.NET predictions can fail due to temporary memory pressure, file system locks, or threading issues.

**Solution:** Retry with exponential backoff gives the system time to recover.

## Retry Strategy

Configure a resilience pipeline with:
- **3 retry attempts** (4 total including initial attempt)
- **Exponential backoff**: 1s  2s  4s between attempts
- **Logging**: Track each retry for observability

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

## Tasks

### Configure Retry Policy

**File:** `Program.cs`

1. Uncomment the retry policy configuration
2. Uncomment the retry wrapper in the `/predict` endpoint: `retryPolicy.Execute(() => modelSvc.Predict(value))`

**Files:** `Program.cs` and `Models.cs` for reference

### Test

Run and verify normal operation:

```powershell
dotnet run
```

Test prediction:

```powershell
$r1 = Invoke-RestMethod "http://localhost:5000/predict/0.7"
$r1
```

**Expected:** Prediction succeeds on first attempt (no retry logs)

## Retry Timing Analysis

| Attempt | Wait Time | Cumulative |
|---------|-----------|------------|
| Initial | 0s | 0s |
| Retry 1 | 1s | 1s |
| Retry 2 | 2s | 3s |
| Retry 3 | 4s | 7s |

**Total worst case:** ~7 seconds before final failure

## When Retry Helps vs. Hurts

| Scenario | Retry Behavior | Outcome |
|----------|---------------|---------|
| Transient network glitch | Attempt 1 fails, Retry 1 succeeds |  User sees success |
| Temporary memory pressure | Attempts fail, system recovers, succeeds |  Resilient |
| Missing model file | All attempts fail immediately |  7s wasted |
| Corrupted model | All attempts fail immediately |  7s wasted per request |

**Key Insight:** Retry is excellent for transient failures but wasteful for persistent problems. Solution: Circuit Breaker (Step 3).

## Next

Proceed to `Step3-AddCircuitBreaker` to fail fast on persistent errors.
