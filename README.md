# Day 2 Starter

## Overview

This folder contains the **complete Day 2 ** using progressive disclosure methodology.

All code is **pre-written and commented out**. Participants uncomment code block by block, following TASKS.md guides in each step.

## Folder Structure

```
Day2-Starter/
├── Step1-BaselineFromDay1/          # Recap of Day 1 (MAPE-K loop)
├── Step2-AddRetryPolicy/            # Retry with exponential backoff
├── Step3-AddCircuitBreaker/         # Circuit breaker pattern
├── Step4-AddFallback/               # Graceful degradation
├── Step5-AddTimeoutAndBulkhead/     # Timeout + resource isolation
└── Step6-CompleteResilience/        # Complete reference implementation
```

## Learning Path

| Step       | Focus                 | Duration | Key Concepts                            |
| ---------- | --------------------- | -------- | --------------------------------------- |
| **Step 1** | Baseline from Day 1   | 15 min   | MAPE-K loop, why resilience matters     |
| **Step 2** | Retry                 | 20 min   | Transient failures, exponential backoff |
| **Step 3** | Circuit Breaker       | 25 min   | Fail fast, circuit states, policy order |
| **Step 4** | Fallback              | 20 min   | Graceful degradation, observability     |
| **Step 5** | Timeout + Bulkhead    | 30 min   | Resource protection, concurrency limits |
| **Step 6** | Complete Architecture | 20 min   | Review, production considerations       |

## Each Step Contains

- **{StepName}.csproj** - Project file with necessary packages
- **Program.cs** - All code commented out with TASK markers
- **TASKS.md** - Step-by-step uncomment guide with discussion prompts
- **test-commands.md** - PowerShell commands to test functionality

**Step 6 is different:**

- **Program.cs** - Complete uncommented reference implementation
- **ARCHITECTURE.md** - System design and patterns
- **README.md** - Production checklist and next steps

## Teaching Methodology

### Progressive Disclosure Approach

**Traditional approach problems:**

- ❌ Participants copy-paste code
- ❌ Typos break everything
- ❌ Time wasted debugging syntax
- ❌ Focus on mechanics, not concepts

**Progressive disclosure solution:**

- ✅ Code is pre-written and correct
- ✅ Participants uncomment block by block
- ✅ Immediate success, no debugging
- ✅ Focus on concepts and discussion

### Discussion points

Each TASKS.md includes discussion questions:

**Step 2 (Retry):**

- "What if the problem is permanent?"
- "How many retries is too many?"

**Step 3 (Circuit Breaker):**

- "Why fail fast instead of retry?"
- "What happens when circuit opens?"

**Step 4 (Fallback):**

- "When is degraded service better than no service?"
- "How do you know fallback is being used?"

**Step 5 (Timeout + Bulkhead):**

- "Why timeout before retry?"
- "When should you use bulkhead?"

## Resilience Patterns Summary

| Pattern             | Protects Against    | HTTP Status    | Policy Order      |
| ------------------- | ------------------- | -------------- | ----------------- |
| **Timeout**         | Hanging requests    | 504            | 1st (outermost)   |
| **Retry**           | Transient failures  | N/A            | 2nd               |
| **Circuit Breaker** | Cascading failures  | 503 → Fallback | 3rd               |
| **Fallback**        | Complete outages    | 200 (degraded) | Exception handler |
| **Bulkhead**        | Resource exhaustion | 429            | Separate pipeline |

## Testing Strategy

### Normal Operation

All steps have smoke tests in test-commands.md:

```powershell
$r1 = Invoke-RestMethod -Uri "http://localhost:5000/predict/0.7"
$o1 = $r1.observationId
Invoke-WebRequest -Uri "http://localhost:5000/label/${o1}?actualAlert=true" -Method POST
Invoke-RestMethod -Uri "http://localhost:5000/stats"
```

### Advanced Testing

Steps 3-5 include optional chaos testing:

- Force failures to trigger circuit breaker
- Simulate slow operations to test timeout
- Concurrent requests to test bulkhead

## Prerequisites

Participants should have completed:

- ✅ Day 1 (ML.NET, Serilog, OpenTelemetry basics)
- ✅ PowerShell basics
- ✅ .NET 8 SDK installed
- ✅ VS Code or Visual Studio

## Package Dependencies

All steps use:

- Microsoft.ML 3.0.1
- Serilog.AspNetCore 8.0.1
- OpenTelemetry.\* 1.7.x
- Polly 8.2.0 (Steps 2-6)

## Common Issues

### Issue: "Package Polly 8.2.0 not found"

**Fix:** Restore packages

```powershell
dotnet restore
```

### Issue: "Cannot start on port 5000"

**Fix:** Kill existing process or change port in Program.cs

### Issue: "Model file not found"

**Fix:** Model is in-memory only, no file needed

### Issue: Participants get ahead

**Strategy:**

- Emphasize discussion over speed
- Ask participants to explain code they uncommented
- Use "check for understanding" questions

## Comparison to Day1-Starter

| Aspect       | Day 1                    | Day 2               |
| ------------ | ------------------------ | ------------------- |
| Focus        | Observability + Learning | Resilience          |
| Steps        | 6                        | 6                   |
| Key Library  | ML.NET                   | Polly               |
| Architecture | MAPE-K loop              | Resilience patterns |
| Complexity   | Medium                   | High                |
