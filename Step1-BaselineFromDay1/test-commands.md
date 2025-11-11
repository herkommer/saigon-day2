# Step 1 - Test Commands

## Prerequisites

```powershell
dotnet run
```

API will start on `http://localhost:5000`

## MAPE Loop Test Sequence

### 1. Monitor - Make a prediction

```powershell
$r1 = Invoke-RestMethod "http://localhost:5000/predict/0.7"
$r1
```

**Expected:**

```
observationId  : <GUID>
value          : 0.7
shouldAlert    : True
probability    : 0.XX
modelVersion   : 1
```

### 2. Analyze - Provide ground truth

```powershell
Invoke-WebRequest "http://localhost:5000/label/$($r1.observationId)?actualAlert=true" -Method POST | ConvertFrom-Json
```

**Expected:**

```
observationId : <GUID>
predicted     : True
actual        : True
wasCorrect    : True
```

### 3. Plan - Check model statistics

```powershell
Invoke-RestMethod "http://localhost:5000/stats"
```

**Expected:**

```
modelVersion         : 1
totalPredictions     : 1
labeledPredictions   : 1
correctPredictions   : 1
accuracy             : 100
readyForRetraining   : False
```

### 4. Execute - Trigger model retraining

Make additional predictions and label them (minimum 3 total):

```powershell
$r2 = Invoke-RestMethod "http://localhost:5000/predict/0.1"
$r3 = Invoke-RestMethod "http://localhost:5000/predict/0.5"
$r4 = Invoke-RestMethod "http://localhost:5000/predict/0.9"

Invoke-WebRequest "http://localhost:5000/label/$($r2.observationId)?actualAlert=false" -Method POST
Invoke-WebRequest "http://localhost:5000/label/$($r3.observationId)?actualAlert=false" -Method POST
Invoke-WebRequest "http://localhost:5000/label/$($r4.observationId)?actualAlert=true" -Method POST
```

Initiate retraining:

```powershell
Invoke-WebRequest "http://localhost:5000/retrain" -Method POST | ConvertFrom-Json
```

**Expected:**

```
success          : True
oldVersion       : 1
newVersion       : 2
trainingSamples  : 4
```

Verify new model version:

```powershell
$r5 = Invoke-RestMethod "http://localhost:5000/predict/0.7"
$r5.modelVersion  # Should be 2
```

## Quick Smoke Test

```powershell
$r1 = Invoke-RestMethod "http://localhost:5000/predict/0.7"
$r1
Invoke-WebRequest "http://localhost:5000/label/$($r1.observationId)?actualAlert=true" -Method POST | ConvertFrom-Json
Invoke-RestMethod "http://localhost:5000/stats"
```
