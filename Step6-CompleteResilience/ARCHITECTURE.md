# Step 6 - Complete Resilience Architecture

## Overview

This is the **complete production-ready self-learning API** with all resilience patterns:

✅ **Retry** - Handle transient failures  
✅ **Circuit Breaker** - Fail fast on persistent failures  
✅ **Fallback** - Graceful degradation  
✅ **Timeout** - Prevent hanging requests  
✅ **Bulkhead** - Resource isolation  
✅ **Observability** - Logging + tracing  
✅ **Self-Learning** - MAPE-K loop

## Architecture Diagram

```
┌─────────────────────────────────────────────────────────────────┐
│                         HTTP Request                            │
└───────────────────────────────┬─────────────────────────────────┘
                                │
                    ┌───────────▼──────────┐
                    │   Timeout (5s)       │  Kill slow requests
                    │   (Outermost)        │
                    └───────────┬──────────┘
                                │
                    ┌───────────▼──────────┐
                    │   Retry (3x)         │  Exponential backoff
                    │   1s → 2s → 4s       │
                    └───────────┬──────────┘
                                │
                    ┌───────────▼──────────┐
                    │   Circuit Breaker    │  Open after 50% failures
                    │   Break: 30s         │
                    └───────────┬──────────┘
                                │
                    ┌───────────▼──────────┐
                    │   ML Prediction      │  Your code
                    │   (Protected!)       │
                    └───────────┬──────────┘
                                │
                   ┌────────────▼───────────────┐
                   │ Success → Return result    │
                   └────────────────────────────┘
                                │
              ┌─────────────────┼─────────────────┐
              │                 │                 │
    ┌─────────▼────────┐  ┌────▼──────┐  ┌──────▼─────────┐
    │ TimeoutException │  │ Circuit   │  │ Other          │
    │ → 504 Timeout    │  │ Open      │  │ Exception      │
    └──────────────────┘  │ → Fallback│  │ → Fallback     │
                          └───────────┘  └────────────────┘
```

## Request Flow

### Scenario 1: Happy Path (Normal Operation)

```
1. Request arrives: /predict/0.7
2. Timeout: Starts 5-second timer ✓
3. Retry: Executes code ✓
4. Circuit Breaker: Closed (normal) ✓
5. ML Prediction: Completes in 50ms ✓
6. Response: { predictedAlert: true, confidence: 0.85, fallbackUsed: false }
```

**Total time:** ~50ms

### Scenario 2: Transient Failure (Retry Helps)

```
1. Request arrives: /predict/0.7
2. Timeout: Starts 5-second timer ✓
3. Retry: Attempts execution
4. Circuit Breaker: Closed ✓
5. ML Prediction: Throws exception ❌
6. Retry: Wait 1 second, retry ✓
7. ML Prediction: Success! ✓
8. Response: { predictedAlert: true, confidence: 0.85, fallbackUsed: false }
```

**Total time:** ~1 second (1 retry)

### Scenario 3: Slow Operation (Timeout Kills It)

```
1. Request arrives: /predict/0.7
2. Timeout: Starts 5-second timer ✓
3. Retry: Attempts execution
4. Circuit Breaker: Closed ✓
5. ML Prediction: Takes 6 seconds... ⏳
6. Timeout: 5 seconds elapsed → KILL ❌
7. Response: 504 Gateway Timeout
```

**Total time:** 5 seconds (timeout limit)

### Scenario 4: Circuit Open (Fallback Saves the Day)

```
1. Request arrives: /predict/0.7
2. Timeout: Starts 5-second timer ✓
3. Retry: Attempts execution
4. Circuit Breaker: OPEN (too many failures) ❌
5. BrokenCircuitException thrown
6. Fallback: threshold > 0.6 → true ✓
7. Response: { predictedAlert: true, confidence: 0.5, modelVersion: -1, fallbackUsed: true }
```

**Total time:** ~1ms (fail fast!)

### Scenario 5: Bulkhead Protection (Retrain)

```
1. Retrain request arrives
2. Bulkhead: Slot 1 available ✓
3. Training starts (10 seconds)

(Another retrain request arrives)
4. Bulkhead: Slot 2 available ✓
5. Training starts (10 seconds)

(Third retrain request arrives)
6. Bulkhead: No slots, add to queue (position 1) ✓
7. Wait for slot...

(8th retrain request arrives)
8. Bulkhead: Queue full (5 max) ❌
9. Response: 429 Too Many Requests
```

## 📊 Resilience Patterns Summary

| Pattern             | Protects Against    | HTTP Status    | Key Setting                  |
| ------------------- | ------------------- | -------------- | ---------------------------- |
| **Timeout**         | Hanging requests    | 504            | 5 seconds                    |
| **Retry**           | Transient failures  | N/A            | 3 attempts, exponential      |
| **Circuit Breaker** | Cascading failures  | 503 → Fallback | 50% failure ratio, 30s break |
| **Fallback**        | Complete outages    | 200 (degraded) | Simple threshold rule        |
| **Bulkhead**        | Resource exhaustion | 429            | 2 concurrent, 5 queued       |

## Key Insights

### Insight 1: Defense in Depth

```
Layer 1: Timeout → "Don't wait forever"
Layer 2: Retry → "Try again, it might work"
Layer 3: Circuit Breaker → "Stop trying if it's broken"
Layer 4: Fallback → "Provide something, even if degraded"
```

**Each layer protects against different failure modes!**

### Insight 2: Policy Order Matters

```
Timeout (outermost)
  ↓
Retry
  ↓
Circuit Breaker
  ↓
Your Code (innermost)
```

**Execution flows from outer to inner:**

- Timeout sets the deadline
- Retry handles transient failures within that deadline
- Circuit breaker counts failures after retries
- Your code is fully protected!

### Insight 3: Observability Is Critical

Every policy has callbacks:

- `OnTimeout` → Log timeout events
- `OnRetry` → Log retry attempts
- `OnOpened/OnClosed` → Log circuit state changes
- `OnRejected` → Log bulkhead rejections

**In production:** These become metrics, alerts, and dashboards!

## Production Considerations

### Monitoring

**Key metrics to track:**

```
# Request metrics
http_requests_total
http_request_duration_seconds

# Resilience metrics
timeout_total
retry_attempts_total
circuit_breaker_state (0=closed, 1=open, 2=half-open)
fallback_used_total
bulkhead_rejected_total

# Business metrics
predictions_total
predictions_accuracy
model_version
```

### Alerting

**Critical alerts:**

```
1. Circuit breaker open for > 1 minute
   → Page on-call engineer

2. Timeout rate > 5%
   → Investigate slow queries

3. Fallback usage > 10%
   → System degraded, investigate

4. Bulkhead rejection rate > 1%
   → Scale up resources
```

### Configuration

**Make these configurable (appsettings.json):**

```json
{
  "Resilience": {
    "Timeout": "00:00:05",
    "Retry": {
      "MaxAttempts": 3,
      "InitialDelay": "00:00:01",
      "BackoffType": "Exponential"
    },
    "CircuitBreaker": {
      "FailureRatio": 0.5,
      "SamplingDuration": "00:00:10",
      "MinimumThroughput": 3,
      "BreakDuration": "00:00:30"
    },
    "Bulkhead": {
      "PermitLimit": 2,
      "QueueLimit": 5
    }
  }
}
```

**Why configurable?**

- Different environments need different settings (dev vs prod)
- Tune settings without redeployment
- A/B testing of resilience strategies

## Testing Strategy

### Unit Tests

```csharp
[Fact]
public async Task Timeout_Should_Kill_Slow_Requests()
{
    // Arrange: Policy with 1-second timeout
    var policy = CreateTimeoutPolicy(TimeSpan.FromSeconds(1));

    // Act: Execute slow operation (2 seconds)
    var exception = await Assert.ThrowsAsync<TimeoutRejectedException>(() =>
        policy.ExecuteAsync(async () => await Task.Delay(2000))
    );

    // Assert
    Assert.NotNull(exception);
}
```

### Integration Tests

```csharp
[Fact]
public async Task CircuitBreaker_Should_Open_After_Failures()
{
    // Arrange: Trigger 10 failures
    for (int i = 0; i < 10; i++)
    {
        await Assert.ThrowsAsync<Exception>(() =>
            client.GetAsync("/predict/invalid")
        );
    }

    // Act: Next request should fail fast
    var response = await client.GetAsync("/predict/0.7");

    // Assert: Circuit is open, fallback used
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    var result = await response.Content.ReadFromJsonAsync<PredictionResult>();
    Assert.True(result.FallbackUsed);
}
```

### Chaos Engineering

**Use tools like:**

- **Chaos Monkey:** Random service failures
- **Latency injection:** Simulate slow dependencies
- **Network partition:** Test circuit breaker

**Goal:** Verify resilience under realistic failure conditions!

## Further Learning

### Books

- "Release It!" by Michael Nygard
- "Site Reliability Engineering" by Google

### Patterns

- Bulkhead pattern (ship compartments)
- Circuit breaker pattern (electrical breaker)
- MAPE-K loop (autonomic computing)

### Tools

- Polly (resilience library we used)
- Steeltoe (cloud-native patterns)
- Resilience4j (Java equivalent)

## Day 2 Complete!

You've learned:

- ✅ **Step 1:** Baseline from Day 1 (MAPE-K loop)
- ✅ **Step 2:** Retry for transient failures
- ✅ **Step 3:** Circuit breaker for persistent failures
- ✅ **Step 4:** Fallback for graceful degradation
- ✅ **Step 5:** Timeout + bulkhead for resource protection
- ✅ **Step 6:** Complete architecture (this step!)
