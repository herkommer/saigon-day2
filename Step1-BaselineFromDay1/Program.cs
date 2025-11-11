using Serilog;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using System.Diagnostics;

// ============================================================================
// TASK 1: Configure Serilog
// ============================================================================

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog();

// ============================================================================
// TASK 2: Configure OpenTelemetry
// ============================================================================

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("AlertPredictor"))
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddSource("AlertPredictor")
        .AddConsoleExporter());

// ============================================================================
// TASK 3: Register services
// ============================================================================

builder.Services.AddSingleton<ObservationStore>();
builder.Services.AddSingleton<ModelService>();

var app = builder.Build();

// ============================================================================
// TASK 4: Initialize the ML model
// ============================================================================

var modelService = app.Services.GetRequiredService<ModelService>();
modelService.TrainInitialModel();

var activitySource = new ActivitySource("AlertPredictor");

// ============================================================================
// TASK 5: Uncomment the /predict endpoint
// ============================================================================

// app.MapGet("/predict/{value:float}", (float value, ObservationStore store, ModelService modelSvc) =>
// {
//     using var activity = activitySource.StartActivity("PredictAlert", ActivityKind.Internal);
//     activity?.SetTag("input.value", value);
//     activity?.SetTag("model.version", modelSvc.ModelVersion);
//
//     var prediction = modelSvc.Predict(value);
//
//     var observation = new Observation
//     {
//         Id = Guid.NewGuid(),
//         Value = value,
//         PredictedAlert = prediction.ShouldAlert,
//         Probability = prediction.Probability,
//         Timestamp = DateTime.UtcNow
//     };
//     store.Add(observation);
//
//     activity?.SetTag("prediction.alert", prediction.ShouldAlert);
//     activity?.SetTag("prediction.probability", prediction.Probability);
//     activity?.SetTag("observation.id", observation.Id);
//
//     Log.Information(
//         "Prediction: Value={Value}, Alert={Alert}, Probability={Probability:F2}, ObservationId={ObservationId}, ModelVersion={ModelVersion}",
//         value, prediction.ShouldAlert, prediction.Probability, observation.Id, modelSvc.ModelVersion);
//
//     return new
//     {
//         observationId = observation.Id,
//         value,
//         shouldAlert = prediction.ShouldAlert,
//         probability = prediction.Probability,
//         modelVersion = modelSvc.ModelVersion
//     };
// });

// ============================================================================
// TASK 6: Uncomment the /label endpoint
// ============================================================================

// app.MapPost("/label/{observationId:guid}", (Guid observationId, bool actualAlert, ObservationStore store) =>
// {
//     var observation = store.GetById(observationId);
//     if (observation == null)
//         return Results.NotFound(new { error = "Observation not found" });
//
//     observation.ActualAlert = actualAlert;
//     observation.IsLabeled = true;
//
//     Log.Information(
//         "Labeled observation: Id={ObservationId}, Predicted={Predicted}, Actual={Actual}, Correct={Correct}",
//         observationId, observation.PredictedAlert, actualAlert, observation.PredictedAlert == actualAlert);
//
//     return Results.Ok(new
//     {
//         observationId,
//         predicted = observation.PredictedAlert,
//         actual = actualAlert,
//         wasCorrect = observation.PredictedAlert == actualAlert
//     });
// });

// ============================================================================
// TASK 7: Uncomment the /observations endpoint
// ============================================================================

// app.MapGet("/observations", (ObservationStore store) =>
// {
//     var observations = store.GetAll();
//     var labeled = observations.Where(o => o.IsLabeled).ToList();
//
//     return new
//     {
//         total = observations.Count,
//         labeled = labeled.Count,
//         unlabeled = observations.Count - labeled.Count,
//         observations = observations.OrderByDescending(o => o.Timestamp).Take(10)
//     };
// });

// ============================================================================
// TASK 8: Uncomment the /retrain endpoint
// ============================================================================

// app.MapPost("/retrain", (ObservationStore store, ModelService modelSvc) =>
// {
//     using var activity = activitySource.StartActivity("RetrainModel", ActivityKind.Internal);
//
//     var labeledObs = store.GetLabeled();
//
//     if (labeledObs.Count < 3)
//     {
//         var error = $"Need at least 3 labeled observations to retrain. Current: {labeledObs.Count}";
//         Log.Warning(error);
//         return Results.BadRequest(new { error });
//     }
//
//     var trainingData = labeledObs.Select(o => new SignalData
//     {
//         Value = o.Value,
//         ShouldAlert = o.ActualAlert
//     }).ToArray();
//
//     var oldVersion = modelSvc.ModelVersion;
//     modelSvc.Retrain(trainingData);
//
//     activity?.SetTag("training.samples", trainingData.Length);
//     activity?.SetTag("model.old_version", oldVersion);
//     activity?.SetTag("model.new_version", modelSvc.ModelVersion);
//
//     Log.Information(
//         "Model retrained: OldVersion={OldVersion}, NewVersion={NewVersion}, TrainingSamples={TrainingSamples}",
//         oldVersion, modelSvc.ModelVersion, trainingData.Length);
//
//     return Results.Ok(new
//     {
//         success = true,
//         oldVersion,
//         newVersion = modelSvc.ModelVersion,
//         trainingSamples = trainingData.Length
//     });
// });

// ============================================================================
// TASK 9: Uncomment the /stats endpoint
// ============================================================================

// app.MapGet("/stats", (ObservationStore store, ModelService modelSvc) =>
// {
//     var observations = store.GetAll();
//     var labeled = observations.Where(o => o.IsLabeled).ToList();
//
//     var correct = labeled.Count(o => o.PredictedAlert == o.ActualAlert);
//     var accuracy = labeled.Count > 0 ? (double)correct / labeled.Count : 0.0;
//
//     return new
//     {
//         modelVersion = modelSvc.ModelVersion,
//         totalPredictions = observations.Count,
//         labeledPredictions = labeled.Count,
//         correctPredictions = correct,
//         accuracy = Math.Round(accuracy * 100, 2),
//         readyForRetraining = labeled.Count >= 3
//     };
// });

app.Run();
