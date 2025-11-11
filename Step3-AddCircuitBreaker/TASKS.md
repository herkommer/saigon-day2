# Step 3 - Add Circuit Breaker

## Overview

Implement Polly's circuit breaker pattern to prevent cascading failures and enable fail-fast behavior when the system is degraded.

**Problem:** Retry policies help with transient failures, but when a downstream dependency is persistently broken (corrupted model file, database outage), retrying every request wastes resources and delays failure feedback to clients.

**Solution:** Circuit breaker monitors failure rates and "opens" the circuit after a threshold is crossed, failing requests immediately until the system recovers.

## Circuit Breaker States

| State         | Behavior                                     | Transition Condition                   |
| ------------- | -------------------------------------------- | -------------------------------------- |
| **CLOSED**    | Requests flow normally, failures are counted | Failure ratio exceeds threshold → OPEN |
| **OPEN**      | All requests fail immediately (fail-fast)    | Break duration expires → HALF-OPEN     |
| **HALF-OPEN** | Allow one test request through               | Success → CLOSED, Failure → OPEN       |

**Configuration (Step 3):**

- `FailureRatio`: 0.5 (50% failure rate triggers opening)
- `SamplingDuration`: 10 seconds (measurement window)
- `MinimumThroughput`: 3 requests (minimum sample size)
- `BreakDuration`: 30 seconds (stay open before testing recovery)

## Tasks

### Task 1: Configure Circuit Breaker Policy

Add circuit breaker configuration with state transition logging:

```csharp
var circuitBreakerPolicy = new ResiliencePipelineBuilder()
    .AddCircuitBreaker(new CircuitBreakerStrategyOptions
    {
        FailureRatio = 0.5,
        SamplingDuration = TimeSpan.FromSeconds(10),
        MinimumThroughput = 3,
        BreakDuration = TimeSpan.FromSeconds(30),
        OnOpened = args =>
        {
            Log.Error("Circuit breaker OPENED after {FailureCount} failures", args.BreakDuration);
            return ValueTask.CompletedTask;
        },
        OnClosed = args =>
        {
            Log.Information("Circuit breaker CLOSED - system recovered");
            return ValueTask.CompletedTask;
        },
        OnHalfOpened = args =>
        {
            Log.Warning("Circuit breaker HALF-OPEN - testing if system recovered");
            return ValueTask.CompletedTask;
        }
    })
    .Build();
```

### Task 2: Compose Combined Policy

Chain retry and circuit breaker policies in the correct order:

```csharp
var combinedPolicy = new ResiliencePipelineBuilder()
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
    .AddCircuitBreaker(new CircuitBreakerStrategyOptions
    {
        FailureRatio = 0.5,
        SamplingDuration = TimeSpan.FromSeconds(10),
        MinimumThroughput = 3,
        BreakDuration = TimeSpan.FromSeconds(30),
        OnOpened = args =>
        {
            Log.Error("Circuit breaker OPENED - failing fast for {BreakDuration}s", args.BreakDuration.TotalSeconds);
            return ValueTask.CompletedTask;
        },
        OnClosed = args =>
        {
            Log.Information("Circuit breaker CLOSED - system recovered");
            return ValueTask.CompletedTask;
        },
        OnHalfOpened = args =>
        {
            Log.Warning("Circuit breaker HALF-OPEN - testing if system recovered");
            return ValueTask.CompletedTask;
        }
    })
    .Build();
```

### Task 3: Handle BrokenCircuitException

Wrap `/predict` endpoint with combined policy and handle circuit open state:

```csharp
try
{
    var result = combinedPolicy.Execute(() =>
    {
        // ... prediction logic ...
    });
    return Results.Ok(result);
}
catch (BrokenCircuitException)
{
    Log.Error("Circuit is OPEN - failing fast without retry");
    return Results.StatusCode(503);
}
```

## Analysis: Retry-Only vs Circuit Breaker

### Scenario: ML Model File Corrupted

| Metric                 | Retry-Only (Step 2)         | Circuit Breaker (Step 3)       |
| ---------------------- | --------------------------- | ------------------------------ |
| **Request 1**          | 3 retries × 1s = 7s failure | 3 retries × 1s = 7s failure    |
| **Request 2**          | 3 retries × 1s = 7s failure | 3 retries × 1s = 7s failure    |
| **Request 3**          | 3 retries × 1s = 7s failure | Circuit opens → 0.001s failure |
| **Requests 4-100**     | 97 × 7s = 679s wasted       | 97 × 0.001s = instant fail     |
| **Total Time**         | 700 seconds                 | 14 seconds + negligible        |
| **Thread Pool Impact** | 300 threads blocked         | 6 threads blocked              |
| **Client Experience**  | All requests wait 7s        | First 2 wait, rest fail fast   |

### Policy Composition Order

| Order                       | Execution Flow                             | Circuit Behavior                      | Use Case                                         |
| --------------------------- | ------------------------------------------ | ------------------------------------- | ------------------------------------------------ |
| **Retry → Circuit Breaker** | Request → Retry (3×) → CB counts 1 failure | Opens less frequently, more forgiving | Default choice - give system max recovery chance |
| **Circuit Breaker → Retry** | Request → CB counts 1 failure → Retry (3×) | Opens more aggressively               | Protect downstream systems with strict SLAs      |

**Step 3 uses Retry → Circuit Breaker** to maximize self-healing while preventing cascading failures.

## Key Insight: Observability Through State Transitions

Circuit breaker state transitions provide critical production signals:

**OnOpened Event:**

- **Local:** Log error with context (failure rate, sampling window)
- **Metrics:** Increment `circuit_breaker_opens_total` counter
- **Alerting:** Page on-call engineer (system is degraded)
- **Health Check:** Mark service as unhealthy for load balancer removal

**OnClosed Event:**

- **Local:** Log recovery confirmation
- **Metrics:** Record `circuit_breaker_closed_timestamp`
- **Alerting:** Clear pages, notify recovery
- **Health Check:** Mark service as healthy

**Production Impact:**

- Fail-fast prevents thread pool exhaustion
- State transitions trigger automated responses
- Reduced mean time to detection (MTTD)
- Clear separation between "trying to recover" vs "system is down"

## Next Step

**Limitation:** Circuit open returns 503 (Service Unavailable) to clients.

**Improvement:** Step 4 adds fallback strategy to return degraded predictions (conservative threshold-based logic) instead of hard failures, improving user experience during outages.
