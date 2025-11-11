# Step 4 - Add Fallback Strategy

## Overview

Implement graceful degradation with Polly's fallback pattern to provide degraded service instead of hard failures when the ML model is unavailable.

**Problem:** Circuit breaker (Step 3) returns 503 errors when open, causing complete service outage from the user's perspective.

**Solution:** Fallback pattern catches failures and returns conservative predictions using a simple threshold rule instead of failing completely.

## Fallback Behavior

### Service Degradation Levels

| Level                | State                              | Response                                  | User Experience                     |
| -------------------- | ---------------------------------- | ----------------------------------------- | ----------------------------------- |
| **Full Service**     | Circuit CLOSED, ML model available | ML predictions (confidence 0.7-0.9)       | Optimal accuracy                    |
| **Degraded Service** | Circuit OPEN or model error        | Threshold rule: `threshold > 0.6` → alert | Reduced accuracy, conservative bias |
| **No Service**       | No fallback implemented            | 503 Service Unavailable                   | Complete outage                     |

**Step 4 keeps you at "Degraded Service" instead of "No Service".**

### Fallback Response Structure

```json
{
  "predictedAlert": true,
  "confidence": 0.5,
  "observationId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "modelVersion": -1,
  "fallbackUsed": true,
  "fallbackReason": "Circuit breaker open or model unavailable"
}
```

**Key indicators:**

- `modelVersion: -1` signals fallback mode
- `confidence: 0.5` (always) indicates degraded accuracy
- `fallbackUsed: true` enables observability

## Tasks

### Task 1: Create FallbackService (in Models.cs)

**Location:** `Models.cs` at the bottom of the file

**Action:** Uncomment the entire `FallbackService` class

The FallbackService provides conservative predictions when the ML model is unavailable:

```csharp
public class FallbackService
{
    public object GetFallbackPrediction(double threshold)
    {
        var fallbackAlert = threshold > 0.6;

        Log.Warning("Using fallback: threshold {Threshold} → {Alert}", threshold, fallbackAlert);

        return new
        {
            predictedAlert = fallbackAlert,
            confidence = 0.5,
            observationId = Guid.NewGuid(),
            modelVersion = -1,
            fallbackUsed = true,
            fallbackReason = "Circuit breaker open or model unavailable"
        };
    }
}
```

**Design decision:** Threshold 0.6 is conservative (favors false positives over false negatives), appropriate for alert systems where missing an alert is worse than a false alarm.

### Task 2: Register FallbackService (in Program.cs)

**Location:** `Program.cs` around line 38 in the service registration section

**Action:** Uncomment the FallbackService registration line

```csharp
builder.Services.AddSingleton<ObservationStore>();
builder.Services.AddSingleton<ModelService>();
builder.Services.AddSingleton<FallbackService>();  // Uncomment this line
```

### Task 3: Implement Fallback in Prediction Endpoint (in Program.cs)

**Location:** `Program.cs` in the `/predict` endpoint (around line 130-200)

**Action:** This task has 4 parts - follow the TODO comments in the code:

#### Part A: Add FallbackService Parameter

Uncomment the `FallbackService fallbackService` parameter in the endpoint signature:

```csharp
app.MapGet("/predict/{threshold:double}", (double threshold, ObservationStore observationStore,
    ModelService modelService, FallbackService fallbackService) =>  // Uncomment fallbackService parameter
```

#### Part B: Add fallbackUsed Field

Add the `fallbackUsed = false` field to the success response object:

```csharp
return new
{
    predictedAlert = observation.PredictedAlert,
    confidence = observation.Confidence,
    observationId = observation.ObservationId,
    modelVersion = observation.ModelVersion,
    fallbackUsed = false  // Add this line
};
```

#### Part C: Handle BrokenCircuitException

In the `catch (BrokenCircuitException)` block:

1. **Uncomment** the 4 lines that use fallback
2. **Delete** the 3 lines that return 503 error

```csharp
catch (BrokenCircuitException)
{
    // Uncomment these 4 lines:
    Log.Warning("Circuit is OPEN - using fallback");
    activity?.SetTag("circuit_open", true);
    activity?.SetTag("fallback_used", true);
    return Results.Ok(fallbackService.GetFallbackPrediction(threshold));

    // Delete these 3 lines (old code):
    // Log.Error("Circuit is OPEN - failing fast without fallback");
    // activity?.SetTag("circuit_open", true);
    // return Results.StatusCode(503);
}
```

#### Part D: Handle General Exceptions

In the `catch (Exception ex)` block:

1. **Uncomment** the 4 lines that use fallback
2. **Delete** the 2 lines that return error

```csharp
catch (Exception ex)
{
    // Uncomment these 4 lines:
    Log.Error(ex, "Prediction failed - using fallback");
    activity?.SetTag("error", true);
    activity?.SetTag("fallback_used", true);
    return Results.Ok(fallbackService.GetFallbackPrediction(threshold));

    // Delete these 2 lines (old code):
    // Log.Error(ex, "Prediction failed - no fallback configured yet");
    // return Results.Problem("Prediction failed: " + ex.Message);
}
```

**Exception handling strategy:**

- `BrokenCircuitException`: Expected failure (circuit open) → Log as WARNING
- `Exception`: Unexpected failure (model error) → Log as ERROR
- Both cases: Return fallback instead of propagating error

## Analysis: Fallback Pattern Trade-offs

### Fallback Strategy Comparison

| Strategy                      | Implementation Complexity       | Accuracy | Availability               | Latency  |
| ----------------------------- | ------------------------------- | -------- | -------------------------- | -------- |
| **Static Threshold** (Step 4) | Low - simple rule               | 70-80%   | 100%                       | <1ms     |
| **Cached Model**              | Medium - requires serialization | 85-95%   | 95% (cache miss scenarios) | 1-5ms    |
| **Backup Service**            | High - separate infrastructure  | 90-98%   | 99% (dual dependency)      | 50-100ms |
| **No Fallback** (Step 3)      | None                            | N/A      | 0% (during outages)        | N/A      |

**Step 4 uses Static Threshold** for maximum simplicity and availability.

### Error Response Evolution

| Step                  | Circuit Breaker State    | Response            | HTTP Status | User Impact                              |
| --------------------- | ------------------------ | ------------------- | ----------- | ---------------------------------------- |
| **Step 2 (Retry)**    | N/A - no circuit breaker | Error after retries | 500         | Slow failures (7+ seconds)               |
| **Step 3 (Circuit)**  | OPEN                     | Error immediately   | 503         | Fast failures (<1ms) but complete outage |
| **Step 4 (Fallback)** | OPEN                     | Degraded prediction | 200         | Service continues with reduced accuracy  |

**Key improvement:** HTTP 200 with degraded data is preferable to HTTP 503 for user experience.

## Key Insight: Observability and Progressive Degradation

Fallback responses include multiple observability signals:

**For Real-Time Monitoring:**

```csharp
activity?.SetTag("fallback_used", true);
activity?.SetTag("circuit_open", true);
```

Enable filtering distributed traces by degraded state in Jaeger/Zipkin.

**For Metrics and Alerting:**

- Track `fallback_predictions_total` counter
- Alert when `fallback_rate > 10%` (indicates system degradation)
- Dashboard showing ML vs fallback prediction ratio

**For Client Awareness:**

```json
{
  "fallbackUsed": true,
  "fallbackReason": "Circuit breaker open or model unavailable"
}
```

Clients can display degraded mode indicators in UI or adjust behavior.

**Production Pattern:**

```
Normal Operation:     [ML Model] → High confidence → Full features
Degraded Operation:   [Fallback] → Low confidence → Limited features + Warning banner
Complete Failure:     [503 Error] → No confidence → Error page
```

Progressive degradation provides better user experience than binary success/failure.

## Next Step

**Achieved:** System now gracefully degrades during ML model failures.

**Remaining Challenges:**

- Slow requests can still block threads (no timeout protection)
- Multiple concurrent retrain operations can overwhelm system (no resource isolation)

**Step 5** adds timeout and bulkhead patterns to complete the resilience architecture.
