# Step 3 - Test Commands

## Prerequisites

```powershell
dotnet run
```

API starts on `http://localhost:5000`

## MAPE Loop Testing

### Monitor: Verify Circuit Breaker Configuration

```powershell
Invoke-RestMethod -Uri "http://localhost:5000/circuit-status"
```

**Expected output:**

```json
{
  "message": "Circuit breaker is active",
  "config": {
    "failureRatio": 0.5,
    "samplingDuration": "10 seconds",
    "minimumThroughput": 3,
    "breakDuration": "30 seconds"
  },
  "note": "Circuit state is logged when it opens/closes. Check logs for current state."
}
```

**Interpretation:** Circuit will open when 50% of requests fail within a 10-second window (minimum 3 requests), then stay open for 30 seconds before testing recovery.

### Analyze: Normal Operation (Circuit Stays Closed)

```powershell
$r1 = Invoke-RestMethod -Uri "http://localhost:5000/predict/0.7"
$r1
```

**Expected output:**

```json
{
  "predictedAlert": true,
  "confidence": 0.7,
  "observationId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "modelVersion": 1
}
```

**Expected logs:**

```
[INF] Starting prediction with circuit breaker + retry for threshold 0.7
[INF] Executing prediction (attempt in progress)
[INF] Prediction successful: True with confidence 0.7
```

**Pattern Behavior:** During healthy operation, circuit breaker remains transparent (CLOSED state). All requests flow through normally with no performance overhead.

### Plan: Generate Training Data

```powershell
# Low threshold predictions (should predict no alert)
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

# High threshold predictions (should predict alert)
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

## Circuit Breaker State Transitions

### When Circuit Opens

**Trigger conditions:**

- Minimum 3 requests within 10-second window
- 50% or more fail after retries
- Example: 2 failures + 1 success = 66% failure rate → Circuit OPENS

**Request flow when OPEN:**

```
Request → Circuit Breaker (OPEN) → BrokenCircuitException → 503 Service Unavailable
```

**Expected logs:**

```
[ERR] Circuit breaker OPENED - failing fast for 30s
[ERR] Circuit is OPEN - failing fast without retry
```

**Key behavior:** Retry policy is bypassed. Requests fail in <1ms instead of 7+ seconds.

### Recovery Flow (After 30 Seconds)

**State progression:**

```
OPEN (30s) → HALF-OPEN (test request) → SUCCESS → CLOSED
                                      → FAILURE → OPEN (30s)
```

**Expected logs (successful recovery):**

```
[WRN] Circuit breaker HALF-OPEN - testing if system recovered
[INF] Prediction successful...
[INF] Circuit breaker CLOSED - system recovered
```

## Quick Smoke Test

```powershell
# 1. Check circuit configuration
Invoke-RestMethod -Uri "http://localhost:5000/circuit-status"

# 2. Make prediction
$r = Invoke-RestMethod -Uri "http://localhost:5000/predict/0.7"
$r

# 3. Label observation
Invoke-WebRequest -Uri "http://localhost:5000/label/$($r.observationId)?actualAlert=true" -Method POST | Select-Object -ExpandProperty Content | ConvertFrom-Json

# 4. Check stats
Invoke-RestMethod -Uri "http://localhost:5000/stats"
```

**Expected:** All endpoints respond successfully. Circuit remains CLOSED (no failures).

## Observability: Circuit State Logging

**OnOpened:**

```
[ERR] Circuit breaker OPENED - failing fast for 30s
```

**Production actions:** Increment metrics, trigger alerts, mark unhealthy for load balancer

**OnClosed:**

```
[INF] Circuit breaker CLOSED - system recovered
```

**Production actions:** Clear alerts, record recovery time, mark healthy

**OnHalfOpened:**

```
[WRN] Circuit breaker HALF-OPEN - testing if system recovered
```

**Production actions:** Log test attempt, prepare for state transition

## Success Criteria

- ✅ `/circuit-status` returns configuration
- ✅ Normal predictions succeed (circuit stays CLOSED)
- ✅ Model retraining updates version
- ✅ All MAPE loop operations complete
- ✅ State transition callbacks present in code
- ✅ `BrokenCircuitException` handler returns 503

## Next Step

Step 4 adds fallback strategy to return degraded predictions (threshold-based heuristics) instead of 503 errors when the circuit opens, improving availability during outages.
