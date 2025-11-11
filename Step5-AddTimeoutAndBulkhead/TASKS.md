# Step 5 - Add Timeout and Bulkhead Patterns

## Overview

Implement timeout policy to prevent hanging requests and bulkhead (concurrency limiter) to isolate resource-intensive operations.

**Problems:**

- **Hanging requests:** Slow operations can block threads indefinitely, leading to thread pool exhaustion
- **Resource exhaustion:** Concurrent resource-intensive operations (ML training) can cause memory overflow

**Solutions:**

- **Timeout:** Kill requests exceeding time threshold to free threads
- **Bulkhead:** Limit concurrent operations to prevent resource saturation

## Pattern Comparison

### Timeout vs No Timeout

| Scenario                | Without Timeout                                     | With Timeout (5s)                      |
| ----------------------- | --------------------------------------------------- | -------------------------------------- |
| **Normal request (1s)** | Completes in 1s                                     | Completes in 1s                        |
| **Slow request (60s)**  | Blocks thread for 60s                               | Killed after 5s, thread freed          |
| **10 slow requests**    | 10 threads blocked for 60s = thread pool exhaustion | All killed after 5s, threads available |
| **User experience**     | Unpredictable wait times (1s to 60s+)               | Predictable max wait (5s)              |

### Bulkhead vs No Bulkhead

| Scenario           | Without Bulkhead                      | With Bulkhead (2 concurrent, 5 queued) |
| ------------------ | ------------------------------------- | -------------------------------------- |
| **Request 1**      | Executes immediately                  | Executes immediately                   |
| **Request 2**      | Executes immediately                  | Executes immediately                   |
| **Request 3**      | Executes immediately (⚠️ high memory) | Queued, waits for slot                 |
| **Request 8**      | Executes immediately (💥 OOM crash)   | Rejected with 429 (service survives)   |
| **Resource usage** | Unbounded (dangerous)                 | Bounded, predictable                   |

## Tasks

### Task 1: Configure Timeout Policy (in Program.cs)

**Location:** `Program.cs` after the `combinedPolicy` declaration (around line 125)

**Action:** Uncomment the timeout policy configuration

Add a standalone timeout policy for timeout-specific testing:

```csharp
// var timeoutPolicy = new ResiliencePipelineBuilder()
//     .AddTimeout(new TimeoutStrategyOptions
//     {
//         Timeout = TimeSpan.FromSeconds(5),
//         OnTimeout = args =>
//         {
//             Log.Error("Request timed out after {Timeout} seconds", args.Timeout.TotalSeconds);
//             return ValueTask.CompletedTask;
//         }
//     })
//     .Build();
```

**Configuration rationale:**

- 5-second timeout balances responsiveness vs allowing reasonable processing time
- `OnTimeout` callback enables observability (metrics, alerting)

### Task 2: Configure Bulkhead for Retraining (in Program.cs)

**Location:** `Program.cs` after the timeout policy (around line 138)

**Action:** Uncomment the bulkhead policy configuration

Add concurrency limiter for resource-intensive `/retrain` operations:

```csharp
// var retrainBulkhead = new ResiliencePipelineBuilder()
//     .AddConcurrencyLimiter(new ConcurrencyLimiterOptions
//     {
//         PermitLimit = 2,
//         QueueLimit = 5,
//         OnRejected = args =>
//         {
//             Log.Warning("Retrain rejected - bulkhead full");
//             return ValueTask.CompletedTask;
//         }
//     })
//     .Build();
```

**Configuration rationale:**

- `PermitLimit: 2` - ML training is CPU/memory intensive, limit concurrent operations
- `QueueLimit: 5` - Accept burst traffic but prevent queue overflow
- Request 8+ rejected immediately to prevent unbounded queuing

### Task 3: Add Timeout to Combined Policy (in Program.cs)

**Location:** `Program.cs` in the `combinedPolicy` declaration (around line 92)

**Action:** Add `.AddTimeout()` at the beginning of the policy chain

Modify the existing `combinedPolicy` to include timeout protection:

```csharp
var combinedPolicy = new ResiliencePipelineBuilder()
    .AddTimeout(new TimeoutStrategyOptions  // Add this entire block
    {
        Timeout = TimeSpan.FromSeconds(5),
        OnTimeout = args =>
        {
            Log.Error("Request timed out after {Timeout} seconds", args.Timeout.TotalSeconds);
            return ValueTask.CompletedTask;
        }
    })
    .AddRetry(new RetryStrategyOptions
    {
        // ... existing retry configuration ...
    })
    .AddCircuitBreaker(new CircuitBreakerStrategyOptions
    {
        // ... existing circuit breaker configuration ...
    })
    .Build();
```

**Execution order:** Request → **Timeout** → Retry → Circuit Breaker → Prediction Logic

**Why timeout first?** Ensures maximum 5s total request time regardless of retries.

### Task 4: Handle TimeoutRejectedException (in Program.cs)

**Location:** `Program.cs` in the `/predict` endpoint catch blocks (around line 185)

**Action:** Add a new catch block BEFORE the `BrokenCircuitException` block

Add timeout-specific error handling:

```csharp
try
{
    var result = combinedPolicy.Execute(() =>
    {
        // ... prediction logic ...
    });
    return Results.Ok(result);
}
catch (TimeoutRejectedException)  // Add this entire catch block
{
    Log.Error("Request timed out");
    activity?.SetTag("timeout", true);
    return Results.StatusCode(504); // Gateway Timeout
}
catch (BrokenCircuitException)
{
    // ... existing fallback logic ...
}
catch (Exception ex)
{
    // ... existing fallback logic ...
}
```

**HTTP status codes:**

- `504 Gateway Timeout`: Request exceeded time limit (timeout triggered)
- `200 OK with fallback`: Circuit breaker open or model error (existing fallback)

### Task 5: Wrap Retrain Endpoint with Bulkhead (in Program.cs)

**Location:** `Program.cs` in the `/retrain` endpoint (around line 280)

**Action:** This task has 2 parts:

#### Part A: Wrap Training Logic with Bulkhead

Move the existing training logic inside `retrainBulkhead.Execute()`:

**Before:**

```csharp
app.MapPost("/retrain", (ObservationStore observationStore, ModelService modelService) =>
{
    using var activity = activitySource.StartActivity("RetrainModel");

    Log.Information("Manual retrain requested");

    var labeledObservations = observationStore.GetLabeled();
    // ... training logic ...

    return Results.Ok(new { /* ... */ });
});
```

**After:**

```csharp
app.MapPost("/retrain", (ObservationStore observationStore, ModelService modelService) =>
{
    using var activity = activitySource.StartActivity("RetrainModel");

    try
    {
        var result = retrainBulkhead.Execute(() =>  // Add bulkhead wrapper
        {
            Log.Information("Manual retrain requested");

            var labeledObservations = observationStore.GetLabeled();
            // ... move all training logic inside Execute() ...

            return new
            {
                message = "Model retrained successfully",
                previousVersion,
                newVersion = modelService.CurrentVersion,
                trainingDataCount = trainingData.Length,
                accuracy = metrics.Accuracy
            };
        });

        return Results.Ok(result);
    }
    // ... add exception handlers below ...
});
```

#### Part B: Add Bulkhead Rejection Handler

Add exception handling after the Execute block:

```csharp
    try
    {
        var result = retrainBulkhead.Execute(() =>
        {
            // ... training logic ...
        });

        return Results.Ok(result);
    }
    catch (InvalidOperationException ex) when (ex.Message.Contains("Not enough"))
    {
        return Results.BadRequest(new { message = ex.Message });
    }
    catch (RateLimiterRejectedException)  // Add this catch block
    {
        Log.Warning("Retrain request rejected - bulkhead full");
        activity?.SetTag("bulkhead_rejected", true);
        return Results.StatusCode(429); // Too Many Requests
    }
}
```

**Resource protection:** Bulkhead prevents > 2 concurrent trainings, avoiding memory exhaustion.

## Analysis: Policy Composition Order

### Timeout Position in Stack

| Order                             | Execution Flow                     | 6s Operation Behavior     | Total Time |
| --------------------------------- | ---------------------------------- | ------------------------- | ---------- |
| **Timeout → Retry → CB** (Step 5) | Timeout(5s) wraps entire operation | Killed after 5s, no retry | 5s         |
| **Retry → Timeout → CB**          | Each retry has 5s timeout          | 3 retries × 5s timeout    | 15s        |
| **Retry → CB → Timeout**          | Timeout inside circuit breaker     | Same as above             | 15s        |

**Step 5 uses Timeout → Retry → Circuit Breaker** for fastest failure detection.

### Trade-off Analysis

| Aspect                               | Timeout First                  | Retry First                           |
| ------------------------------------ | ------------------------------ | ------------------------------------- |
| **Max latency**                      | 5s (predictable)               | 15s (3 retries × 5s)                  |
| **Recovery from transient slowness** | ❌ No retry for slow ops       | ✅ Multiple chances                   |
| **Thread pool protection**           | ✅ Fast thread release         | ⚠️ Threads blocked longer             |
| **User experience**                  | ✅ Predictable, fast failures  | ❌ Unpredictable long waits           |
| **Use case**                         | User-facing APIs (UX priority) | Background jobs (completion priority) |

**Production recommendation:** Timeout first for user-facing APIs, Retry first for background processing.

## Key Insight: Bulkhead Compartmentalization

Bulkhead pattern isolates different operation types to prevent cascading failures:

**Without bulkhead:**

```
[/predict] → Uses 100% CPU → [/retrain] starves → Entire API degrades
```

**With bulkhead:**

```
[/predict] → Normal operation
[/retrain] → Isolated pool (max 2 concurrent) → Degrades independently
```

**Production pattern:**

- Separate bulkheads for different operation classes
- Critical operations (user requests) get larger pools
- Resource-intensive operations (batch jobs) get smaller pools

**Example multi-bulkhead architecture:**

```csharp
var userRequestBulkhead = new ResiliencePipelineBuilder()
    .AddConcurrencyLimiter(new ConcurrencyLimiterOptions
    {
        PermitLimit = 100,  // High capacity for user traffic
        QueueLimit = 50
    })
    .Build();

var batchJobBulkhead = new ResiliencePipelineBuilder()
    .AddConcurrencyLimiter(new ConcurrencyLimiterOptions
    {
        PermitLimit = 5,    // Low capacity for expensive jobs
        QueueLimit = 10
    })
    .Build();

var retrainBulkhead = new ResiliencePipelineBuilder()
    .AddConcurrencyLimiter(new ConcurrencyLimiterOptions
    {
        PermitLimit = 2,    // Very low for ML training
        QueueLimit = 5
    })
    .Build();
```

**Result:** System degrades gracefully under load instead of complete failure.

## Observability: Timeout and Bulkhead Metrics

**Timeout monitoring:**

```csharp
OnTimeout = args =>
{
    Log.Error("Request timed out after {Timeout} seconds", args.Timeout.TotalSeconds);
    // Metrics: Increment timeout_total counter
    // Alerting: If timeout_rate > 5%, investigate slow operations
    return ValueTask.CompletedTask;
}
```

**Bulkhead monitoring:**

```csharp
OnRejected = args =>
{
    Log.Warning("Retrain rejected - bulkhead full");
    // Metrics: Increment bulkhead_rejected_total counter
    // Metrics: Track bulkhead_queue_length and bulkhead_active_count
    // Alerting: If rejection_rate > 10%, increase permit limit
    return ValueTask.CompletedTask;
}
```

**Production dashboards:**

- Timeout rate trend (should be < 1% normally)
- P95/P99 latency (should be well under timeout threshold)
- Bulkhead utilization (active permits / total permits)
- Bulkhead rejection rate (should be near 0% during normal operation)

## Next Step

**Achieved:** System now has complete resilience against transient failures, persistent failures, slow operations, and resource exhaustion.

**Step 6** provides the complete reference implementation with all patterns integrated and production-ready architecture documentation.
