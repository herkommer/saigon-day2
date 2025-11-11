using Microsoft.ML;
using Microsoft.ML.Data;
using Serilog;
using System.Collections.Concurrent;

// ============================================================================
// Data Models - From Day 1 Step 6
// ============================================================================

public class SignalData
{
    public float Value { get; set; }
    public bool ShouldAlert { get; set; }
}

public class AlertPrediction
{
    [ColumnName("PredictedLabel")]
    public bool ShouldAlert { get; set; }

    [ColumnName("Probability")]
    public float Probability { get; set; }
}

public class Observation
{
    public Guid Id { get; set; }
    public float Value { get; set; }
    public bool PredictedAlert { get; set; }
    public float Probability { get; set; }
    public DateTime Timestamp { get; set; }
    public bool IsLabeled { get; set; }
    public bool ActualAlert { get; set; }
}

// ============================================================================
// Services - From Day 1 Step 6
// ============================================================================

public class ObservationStore
{
    private readonly ConcurrentBag<Observation> _observations = new();

    public void Add(Observation observation) => _observations.Add(observation);
    public Observation? GetById(Guid id) => _observations.FirstOrDefault(o => o.Id == id);
    public List<Observation> GetAll() => _observations.ToList();
    public List<Observation> GetLabeled() => _observations.Where(o => o.IsLabeled).ToList();
}

public class ModelService
{
    private readonly MLContext _mlContext;
    private PredictionEngine<SignalData, AlertPrediction> _predictionEngine;
    private readonly object _modelLock = new();

    public int ModelVersion { get; private set; } = 0;

    public ModelService()
    {
        _mlContext = new MLContext(seed: 0);
        _predictionEngine = null!;
    }

    public void TrainInitialModel()
    {
        var trainingData = new[]
        {
            new SignalData { Value = 0.1f, ShouldAlert = false },
            new SignalData { Value = 0.2f, ShouldAlert = false },
            new SignalData { Value = 0.3f, ShouldAlert = false },
            new SignalData { Value = 0.4f, ShouldAlert = false },
            new SignalData { Value = 0.6f, ShouldAlert = true },
            new SignalData { Value = 0.7f, ShouldAlert = true },
            new SignalData { Value = 0.8f, ShouldAlert = true },
            new SignalData { Value = 0.9f, ShouldAlert = true },
        };

        Retrain(trainingData);
        Log.Information("Initial model trained (Version {ModelVersion})", ModelVersion);
    }

    public void Retrain(SignalData[] trainingData)
    {
        var dataView = _mlContext.Data.LoadFromEnumerable(trainingData);

        var pipeline = _mlContext.Transforms
            .Concatenate("Features", nameof(SignalData.Value))
            .Append(_mlContext.BinaryClassification.Trainers
                .LbfgsLogisticRegression(labelColumnName: nameof(SignalData.ShouldAlert)));

        var model = pipeline.Fit(dataView);

        var newEngine = _mlContext.Model
            .CreatePredictionEngine<SignalData, AlertPrediction>(model);

        lock (_modelLock)
        {
            _predictionEngine = newEngine;
            ModelVersion++;
        }
    }

    public AlertPrediction Predict(float value)
    {
        var input = new SignalData { Value = value };

        lock (_modelLock)
        {
            return _predictionEngine.Predict(input);
        }
    }
}
