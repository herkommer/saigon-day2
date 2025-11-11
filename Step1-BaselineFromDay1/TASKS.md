# Step 1 - Baseline from Day 1

## Overview

Restore the complete self-learning API from Day 1, Step 6 to establish a baseline before adding Polly resilience patterns.

**Infrastructure:** Serilog, OpenTelemetry, services, and model initialization are already configured in `Program.cs`.

**Your task:** Enable the commented endpoints in `Program.cs` to restore full functionality.

## Tasks

### Review the Architecture

- `Program.cs` - Infrastructure setup (already active)
- `Models.cs` - Data models and services from Day 1

### Enable Endpoints

Uncomment the following endpoints in `Program.cs`:

- `/predict` - Main prediction endpoint (target for resilience in Step 2)
- `/label` - Ground truth feedback
- `/observations` - Observation history
- `/retrain` - Model retraining
- `/stats` - Model performance metrics

### Test

Run the API and execute the full MAPE loop:

```powershell
dotnet run
```

Smoke test (see `test-commands.md` for comprehensive tests):

```powershell
$r1 = Invoke-RestMethod "http://localhost:5000/predict/0.7"
Invoke-WebRequest "http://localhost:5000/label/$($r1.observationId)?actualAlert=true" -Method POST
Invoke-RestMethod "http://localhost:5000/stats"
```

**Expected:** All endpoints functional, MAPE loop operational.

## Production Readiness Gap Analysis

Now that you have a working baseline, consider the production risks:

| Failure Scenario                  | Impact                              | Mitigation (Day 2)                           |
| --------------------------------- | ----------------------------------- | -------------------------------------------- |
| Model prediction takes 30s        | Thread starvation, API unresponsive | Timeout policy (Step 6)                      |
| ML.NET throws transient exception | Immediate 500 error                 | Retry policy (Step 2)                        |
| Model corruption                  | Complete system failure             | Circuit Breaker (Step 3) + Fallback (Step 4) |
| 100 concurrent retrain requests   | Memory exhaustion                   | Bulkhead policy (Step 6)                     |

**Key Insight:** A working system ≠ production-ready system. Day 2 focuses on making this survive production chaos.

## Next

Proceed to `Step2-AddRetryPolicy` to handle transient failures.
