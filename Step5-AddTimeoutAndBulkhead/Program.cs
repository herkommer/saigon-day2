using Microsoft.ML;
using Microsoft.ML.Data;
using Serilog;
using System.Diagnostics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using System.Diagnostics.Metrics;
using System.Collections.Concurrent;
using Polly;
using Polly.Retry;
using Polly.CircuitBreaker;
using Polly.Timeout;

// Configure Serilog
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);

// Suppress Kestrel and Microsoft logs to reduce noise
builder.Logging.ClearProviders();

// Use Serilog for logging
builder.Host.UseSerilog();

// Configure OpenTelemetry
builder.Services.AddOpenTelemetry()
    .WithTracing(tracerProviderBuilder =>
        tracerProviderBuilder
            .AddSource("SelfLearningAPI")
            .AddAspNetCoreInstrumentation()
            .AddConsoleExporter());

// Register services
builder.Services.AddSingleton<ObservationStore>();
builder.Services.AddSingleton<ModelService>();
builder.Services.AddSingleton<FallbackService>();

var app = builder.Build();

// Initialize model
var modelService = app.Services.GetRequiredService<ModelService>();
modelService.TrainInitialModel();

// Activity source for tracing
var activitySource = new ActivitySource("SelfLearningAPI");

// TASK 1: Uncomment the timeout policy below
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

// TASK 2: Uncomment the bulkhead policy below
// var retrainBulkhead = new ResiliencePipelineBuilder()
//     .AddConcurrencyLimiter(new ConcurrencyLimiterOptions
//     {
//         PermitLimit = 2,
//         QueueLimit = 5,
//         OnRejected = args =>
//         {
//             Log.Warning("Retrain request rejected - bulkhead full (limit: 2 concurrent, 5 queued)");
//             return ValueTask.CompletedTask;
//         }
//     })
//     .Build();

// Retry policy from Step 2
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

// Circuit breaker policy from Step 3
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

// TASK 3: Add timeout to the combined policy
// Uncomment the .AddTimeout() section below and add it at the START of the pipeline builder
var combinedPolicy = new ResiliencePipelineBuilder()
    // .AddTimeout(new TimeoutStrategyOptions
    // {
    //     Timeout = TimeSpan.FromSeconds(5),
    //     OnTimeout = args =>
    //     {
    //         Log.Error("Request timed out after {Timeout} seconds", args.Timeout.TotalSeconds);
    //         return ValueTask.CompletedTask;
    //     }
    // })
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
            Log.Warning("Circuit breaker HALF-OPEN - testing recovery");
            return ValueTask.CompletedTask;
        }
    })
    .Build();

// Complete policy with timeout + retry + circuit breaker (for reference)
var predictPolicy = new ResiliencePipelineBuilder()
    .AddTimeout(new TimeoutStrategyOptions
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
            Log.Warning("Circuit breaker HALF-OPEN - testing recovery");
            return ValueTask.CompletedTask;
        }
    })
    .Build();
// Policy order: Timeout → Retry → Circuit Breaker → Your Code

app.MapGet("/predict/{threshold:double}", (double threshold, ObservationStore observationStore, ModelService modelService, FallbackService fallbackService) =>
{
    using var activity = activitySource.StartActivity("PredictWithCompletePolicy");
    activity?.SetTag("threshold", threshold);

    Log.Information("Starting prediction with timeout + retry + circuit breaker for threshold {Threshold}", threshold);

    try
    {
        // This uses combinedPolicy (retry + circuit breaker)
        // After completing Task 3, it will also include timeout
        var result = combinedPolicy.Execute(() =>
        {
            Log.Information("Executing prediction (attempt in progress)");

            var observationId = Guid.NewGuid();
            var mlContext = new MLContext(seed: 42);
            var inputData = new[] { new SignalData { Threshold = (float)threshold } };
            var dataView = mlContext.Data.LoadFromEnumerable(inputData);
            var predictions = modelService.Model.Transform(dataView);
            var predictionResults = mlContext.Data.CreateEnumerable<AlertPrediction>(predictions, reuseRowObject: false);
            var prediction = predictionResults.First();

            var observation = new Observation
            {
                ObservationId = observationId,
                Timestamp = DateTime.UtcNow,
                Threshold = threshold,
                PredictedAlert = prediction.PredictedLabel,
                Confidence = prediction.Probability,
                ModelVersion = modelService.CurrentVersion
            };

            observationStore.Add(observation);

            Log.Information("Prediction successful: {PredictedAlert} with confidence {Confidence}",
                observation.PredictedAlert, observation.Confidence);

            activity?.SetTag("predicted_alert", observation.PredictedAlert);
            activity?.SetTag("confidence", observation.Confidence);
            activity?.SetTag("fallback_used", false);

            return new
            {
                predictedAlert = observation.PredictedAlert,
                confidence = observation.Confidence,
                observationId = observation.ObservationId,
                modelVersion = observation.ModelVersion,
                fallbackUsed = false
            };
        });

        return Results.Ok(result);
    }
    // TASK 4: Add TimeoutRejectedException handler
    // Uncomment the catch block below and place it BEFORE BrokenCircuitException
    // catch (TimeoutRejectedException)
    // {
    //     Log.Error("Request timed out");
    //     activity?.SetTag("timeout", true);
    //     return Results.StatusCode(504); // Gateway Timeout
    // }
    catch (BrokenCircuitException)
    {
        Log.Warning("Circuit is OPEN - using fallback prediction");
        activity?.SetTag("circuit_open", true);
        activity?.SetTag("fallback_used", true);

        var fallbackPrediction = fallbackService.GetFallbackPrediction(threshold);
        return Results.Ok(fallbackPrediction);
    }
    catch (Exception ex)
    {
        Log.Error(ex, "Prediction failed - using fallback");
        activity?.SetTag("error", true);
        activity?.SetTag("fallback_used", true);

        var fallbackPrediction = fallbackService.GetFallbackPrediction(threshold);
        return Results.Ok(fallbackPrediction);
    }
});

app.MapGet("/circuit-status", () =>
{
    Log.Information("Circuit status requested");

    return Results.Ok(new
    {
        message = "Circuit breaker is active",
        config = new
        {
            failureRatio = 0.5,
            samplingDuration = "10 seconds",
            minimumThroughput = 3,
            breakDuration = "30 seconds"
        },
        note = "Circuit state is logged when it opens/closes. Check logs for current state."
    });
});

app.MapPost("/label/{observationId:guid}", (Guid observationId, bool actualAlert, ObservationStore observationStore) =>
{
    using var activity = activitySource.StartActivity("LabelObservation");
    activity?.SetTag("observation_id", observationId);
    activity?.SetTag("actual_alert", actualAlert);

    var observation = observationStore.Get(observationId);
    if (observation == null)
    {
        return Results.NotFound(new { message = "Observation not found" });
    }

    observation.ActualAlert = actualAlert;
    observation.Labeled = true;

    Log.Information("Observation {ObservationId} labeled as {ActualAlert}", observationId, actualAlert);

    return Results.Ok(new { message = "Observation labeled successfully", observationId });
});

app.MapGet("/observations", (ObservationStore observationStore) =>
{
    using var activity = activitySource.StartActivity("GetObservations");

    var observations = observationStore.GetAll()
        .Select(o => new
        {
            observationId = o.ObservationId,
            timestamp = o.Timestamp,
            threshold = o.Threshold,
            predictedAlert = o.PredictedAlert,
            confidence = o.Confidence,
            actualAlert = o.ActualAlert,
            labeled = o.Labeled,
            modelVersion = o.ModelVersion
        });

    return Results.Ok(observations);
});

// TASK 5: Wrap /retrain endpoint with bulkhead (2 parts)
app.MapPost("/retrain", (ObservationStore observationStore, ModelService modelService) =>
{
    using var activity = activitySource.StartActivity("RetrainModel");

    // TASK 5 - Part A: Wrap the training logic below with retrainBulkhead.Execute()
    // Move all the code below (from Log.Information to the return statement) inside:
    // var result = retrainBulkhead.Execute(() => { ... return new { ... }; });

    Log.Information("Manual retrain requested");

    try
    {
        var labeledObservations = observationStore.GetLabeled();
        if (labeledObservations.Count < 10)
        {
            var message = $"Not enough labeled observations. Need 10, have {labeledObservations.Count}";
            Log.Warning(message);
            return Results.BadRequest(new { message });
        }

        var previousVersion = modelService.CurrentVersion;
        var mlContext = new MLContext(seed: 42);
        var trainingData = labeledObservations.Select(o => new SignalData
        {
            Threshold = (float)o.Threshold,
            Alert = o.ActualAlert ?? false
        }).ToArray();

        var dataView = mlContext.Data.LoadFromEnumerable(trainingData);
        var pipeline = mlContext.Transforms.CopyColumns(outputColumnName: "Label", inputColumnName: "Alert")
            .Append(mlContext.Transforms.Concatenate("Features", "Threshold"))
            .Append(mlContext.BinaryClassification.Trainers.SdcaLogisticRegression());

        var newModel = pipeline.Fit(dataView);
        var predictions = newModel.Transform(dataView);
        var metrics = mlContext.BinaryClassification.Evaluate(predictions, "Label");

        modelService.UpdateModel(newModel);

        Log.Information("Model retrained: version {PreviousVersion} -> {NewVersion}, accuracy: {Accuracy}",
            previousVersion, modelService.CurrentVersion, metrics.Accuracy);

        activity?.SetTag("new_version", modelService.CurrentVersion);
        activity?.SetTag("accuracy", metrics.Accuracy);

        return Results.Ok(new
        {
            message = "Model retrained successfully",
            previousVersion,
            newVersion = modelService.CurrentVersion,
            trainingDataCount = trainingData.Length,
            accuracy = metrics.Accuracy
        });
    }
    // TASK 5 - Part B: Add RateLimiterRejectedException handler
    // Uncomment the catch block below:
    // catch (RateLimiterRejectedException)
    // {
    //     Log.Warning("Retrain request rejected - bulkhead full");
    //     activity?.SetTag("bulkhead_rejected", true);
    //     return Results.StatusCode(429); // Too Many Requests
    // }
    catch (Exception ex)
    {
        Log.Error(ex, "Retrain failed");
        return Results.Problem("Retrain failed: " + ex.Message);
    }
});

app.MapGet("/stats", (ObservationStore observationStore) =>
{
    using var activity = activitySource.StartActivity("GetStats");

    var labeled = observationStore.GetLabeled();
    var correct = labeled.Count(o => o.PredictedAlert == o.ActualAlert);
    var accuracy = labeled.Count > 0 ? (double)correct / labeled.Count : 0.0;

    var stats = new
    {
        totalObservations = observationStore.GetAll().Count,
        labeledCount = labeled.Count,
        accuracy,
        currentModelVersion = observationStore.GetAll().FirstOrDefault()?.ModelVersion ?? 1
    };

    Log.Information("Stats requested: {TotalObservations} observations, {LabeledCount} labeled, {Accuracy} accuracy",
        stats.totalObservations, stats.labeledCount, stats.accuracy);

    return Results.Ok(stats);
});

app.Run();
