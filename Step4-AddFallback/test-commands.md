# Step 4 - Test Commands

## Prerequisites

```powershell
dotnet run
```

API starts on `http://localhost:5000`

## MAPE Loop Testing

### Monitor: Verify Fallback Service Configuration

```powershell
# Make a normal prediction and inspect all fields
$r1 = Invoke-RestMethod -Uri "http://localhost:5000/predict/0.7"
$r1 | Format-List
```

**Expected output:**

```
predictedAlert : True
confidence     : 0.7
observationId  : 3fa85f64-5717-4562-b3fc-2c963f66afa6
modelVersion   : 1
fallbackUsed   : False
```

**Interpretation:** `fallbackUsed: False` confirms ML model is active. During healthy operation, fallback remains dormant.

### Analyze: Compare ML Model vs Fallback Logic

**ML Model behavior (current state):**

```powershell
# Low threshold
$r1 = Invoke-RestMethod -Uri "http://localhost:5000/predict/0.2"
$r1.predictedAlert  # ML model prediction (likely False)
$r1.confidence      # High confidence (0.7-0.9)

# High threshold
$r2 = Invoke-RestMethod -Uri "http://localhost:5000/predict/0.8"
$r2.predictedAlert  # ML model prediction (likely True)
$r2.confidence      # High confidence (0.7-0.9)
```

**Fallback threshold rule:** `threshold > 0.6 → alert`

| Threshold | ML Model        | Fallback Rule    | Matches? |
| --------- | --------------- | ---------------- | -------- |
| 0.2       | False (learned) | False (≤0.6)     | ✅       |
| 0.5       | False (learned) | False (≤0.6)     | ✅       |
| 0.6       | False/True      | False (boundary) | ⚠️       |
| 0.7       | True (learned)  | True (>0.6)      | ✅       |
| 0.9       | True (learned)  | True (>0.6)      | ✅       |

**Pattern Behavior:** Fallback provides conservative baseline when ML model unavailable. Less accurate but maintains service availability.

### Plan: Generate Training Data for MAPE Loop

```powershell
# Low threshold observations (no alert expected)
$r1 = Invoke-RestMethod -Uri "http://localhost:5000/predict/0.1"
Invoke-WebRequest -Uri "http://localhost:5000/label/$($r1.observationId)?actualAlert=false" -Method POST | Select-Object -ExpandProperty Content | ConvertFrom-Json

$r2 = Invoke-RestMethod -Uri "http://localhost:5000/predict/0.2"
Invoke-WebRequest -Uri "http://localhost:5000/label/$($r2.observationId)?actualAlert=false" -Method POST | Select-Object -ExpandProperty Content | ConvertFrom-Json

$r3 = Invoke-RestMethod -Uri "http://localhost:5000/predict/0.3"
Invoke-WebRequest -Uri "http://localhost:5000/label/$($r3.observationId)?actualAlert=false" -Method POST | Select-Object -ExpandProperty Content | ConvertFrom-Json

$r4 = Invoke-RestMethod -Uri "http://localhost:5000/predict/0.4"
Invoke-WebRequest -Uri "http://localhost:5000/label/$($r4.observationId)?actualAlert=false" -Method POST | Select-Object -ExpandProperty Content | ConvertFrom-Json

$r5 = Invoke-RestMethod -Uri "http://localhost:5000/predict/0.5"
Invoke-WebRequest -Uri "http://localhost:5000/label/$($r5.observationId)?actualAlert=false" -Method POST | Select-Object -ExpandProperty Content | ConvertFrom-Json

# High threshold observations (alert expected)
$r6 = Invoke-RestMethod -Uri "http://localhost:5000/predict/0.6"
Invoke-WebRequest -Uri "http://localhost:5000/label/$($r6.observationId)?actualAlert=true" -Method POST | Select-Object -ExpandProperty Content | ConvertFrom-Json

$r7 = Invoke-RestMethod -Uri "http://localhost:5000/predict/0.7"
Invoke-WebRequest -Uri "http://localhost:5000/label/$($r7.observationId)?actualAlert=true" -Method POST | Select-Object -ExpandProperty Content | ConvertFrom-Json

$r8 = Invoke-RestMethod -Uri "http://localhost:5000/predict/0.8"
Invoke-WebRequest -Uri "http://localhost:5000/label/$($r8.observationId)?actualAlert=true" -Method POST | Select-Object -ExpandProperty Content | ConvertFrom-Json

$r9 = Invoke-RestMethod -Uri "http://localhost:5000/predict/0.9"
Invoke-WebRequest -Uri "http://localhost:5000/label/$($r9.observationId)?actualAlert=true" -Method POST | Select-Object -ExpandProperty Content | ConvertFrom-Json

$r10 = Invoke-RestMethod -Uri "http://localhost:5000/predict/0.95"
Invoke-WebRequest -Uri "http://localhost:5000/label/$($r10.observationId)?actualAlert=true" -Method POST | Select-Object -ExpandProperty Content | ConvertFrom-Json
```

**Expected output (each label):**

```json
{
  "message": "Observation labeled successfully",
  "observationId": "3fa85f64-5717-4562-b3fc-2c963f66afa6"
}
```

### Execute: Trigger Model Retraining

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

**Expected logs:**

```
[INF] Retrain requested with 10 labeled observations
[INF] Retrained: v1 → v2, accuracy: 0.95
```

### Monitor: Verify Model Version Update

```powershell
$r11 = Invoke-RestMethod -Uri "http://localhost:5000/predict/0.7"
$r11.modelVersion
```

**Expected output:**

```
2
```

## Fallback Activation Testing

### Normal Operation (ML Model Active)

```powershell
$r = Invoke-RestMethod -Uri "http://localhost:5000/predict/0.7"
$r
```

**Expected output:**

```json
{
  "predictedAlert": true,
  "confidence": 0.85,
  "observationId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "modelVersion": 2,
  "fallbackUsed": false
}
```

**Key observation:** `fallbackUsed: false` and high confidence indicate ML model is functioning.

### Fallback Mode Indicators

When fallback activates (circuit open or model error):

**Response structure:**

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

**Degradation signals:**

- `modelVersion: -1` (instead of positive integer)
- `confidence: 0.5` (always, indicates uncertainty)
- `fallbackUsed: true` (explicit flag)
- `fallbackReason` (explains why fallback activated)

### Fallback Threshold Logic

**Threshold rule:** `threshold > 0.6 → predictedAlert = true`

```powershell
# Test boundary conditions (if fallback were active)
# Below threshold
0.5 > 0.6 = False → predictedAlert: false

# At threshold
0.6 > 0.6 = False → predictedAlert: false

# Above threshold
0.7 > 0.6 = True → predictedAlert: true
```

## Quick Smoke Test

```powershell
# 1. Verify ML model is active
$r = Invoke-RestMethod -Uri "http://localhost:5000/predict/0.7"
$r.fallbackUsed  # Should be False

# 2. Label observation
Invoke-WebRequest -Uri "http://localhost:5000/label/$($r.observationId)?actualAlert=true" -Method POST | Select-Object -ExpandProperty Content | ConvertFrom-Json

# 3. Check stats
Invoke-RestMethod -Uri "http://localhost:5000/stats"

# 4. Verify circuit breaker config
Invoke-RestMethod -Uri "http://localhost:5000/circuit-status"
```

**Expected:** All endpoints respond successfully with `fallbackUsed: false`.

**Note:** "Circuit breaker is active" means it's configured and monitoring requests (CLOSED state = healthy). It's not "active" as in "blocking requests" - that only happens when it OPENS due to failures.

## Exception Handling Validation

### Two-Level Exception Handling

**Level 1: BrokenCircuitException (circuit open)**

```
Expected Log: [WRN] Circuit is OPEN - using fallback
HTTP Status: 200 OK (not 503!)
Response: fallbackUsed: true, modelVersion: -1
```

**Level 2: General Exception (model error)**

```
Expected Log: [ERR] Prediction failed - using fallback
HTTP Status: 200 OK
Response: fallbackUsed: true, modelVersion: -1
```

**Key architectural insight:** Both exception types return HTTP 200 with degraded data instead of HTTP 5xx errors, maintaining service availability.

## Observability: Monitoring Fallback Usage

### Distributed Tracing Tags

When fallback activates, OpenTelemetry tags are set:

```csharp
activity?.SetTag("fallback_used", true);
activity?.SetTag("circuit_open", true);  // If BrokenCircuitException
activity?.SetTag("error", true);         // If general Exception
```

**Production usage:** Filter traces in Jaeger by `fallback_used=true` to identify degradation periods.

### Metrics to Track

| Metric               | Query                                          | Alert Threshold |
| -------------------- | ---------------------------------------------- | --------------- |
| Fallback Rate        | `fallback_predictions / total_predictions`     | > 10%           |
| Fallback Accuracy    | `correct_fallback / labeled_fallback`          | < 70%           |
| Mean Confidence      | `avg(confidence) WHERE fallbackUsed=false`     | < 0.7           |
| Degradation Duration | Time between first and last fallback in window | > 5 minutes     |

## Success Criteria

- ✅ Normal predictions return `fallbackUsed: false`
- ✅ FallbackService registered in DI container
- ✅ `/predict` includes `fallbackUsed` field in all responses
- ✅ Exception handlers return HTTP 200 with fallback data
- ✅ Fallback responses include `fallbackReason`
- ✅ `modelVersion: -1` signals degraded mode
- ✅ Confidence always `0.5` during fallback

## Production Considerations

### When Fallback Is Appropriate

| Scenario                       | Primary            | Fallback               | Acceptable?                         |
| ------------------------------ | ------------------ | ---------------------- | ----------------------------------- |
| **E-commerce recommendations** | ML personalization | Popular items          | ✅ Yes - degraded experience OK     |
| **Fraud detection**            | ML risk scoring    | Conservative threshold | ✅ Yes - false positives acceptable |
| **Medical diagnosis**          | ML analysis        | Generic warning        | ❌ No - requires human escalation   |
| **Alert prediction** (Step 4)  | ML model           | Threshold rule         | ✅ Yes - conservative is safe       |

**Decision criteria:** Use fallback when degraded service is better than no service, and false positives are tolerable.

### Fallback Strategy Evolution

| Pattern              | Complexity | Accuracy | Latency  | Availability          |
| -------------------- | ---------- | -------- | -------- | --------------------- |
| Static rule (Step 4) | Low        | 70-80%   | <1ms     | 100%                  |
| Cached model         | Medium     | 85-95%   | 1-5ms    | 95%                   |
| Backup service       | High       | 90-98%   | 50-100ms | 99% (dual dependency) |

**Step 4 uses static rule** for maximum simplicity and availability. Production systems may evolve to cached model for better accuracy.

## Next Step

Step 5 adds timeout and bulkhead patterns to complete the resilience architecture:

- **Timeout**: Prevent slow requests from blocking threads
- **Bulkhead**: Isolate resources to prevent cascading failures across operations

Day 2 Morning session complete. Afternoon session covers advanced resilience patterns.
