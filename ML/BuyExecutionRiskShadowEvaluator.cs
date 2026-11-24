using System;
using System.Collections.Generic;
using System.IO;
using MoneyCreator.Models;
using Newtonsoft.Json;

namespace MoneyCreator.ML
{
    public sealed class BuyExecutionRiskShadowEvaluator
    {
        public const string ExpectedModelKey = "BUY_EXECUTION_RISK_H60_EXTREME";
        public const string ExpectedTarget = "H60_SignalAtDirectionalExtreme";

        private readonly object _sync = new object();
        private ExecutionRiskModelDocument _model;
        private string _loadedPath;
        private DateTime _loadedWriteTimeUtc;
        private string _loadError;

        public ExecutionRiskPrediction Evaluate(FeatureSnapshotRecord feature)
        {
            ExecutionRiskModelDocument model;
            string loadError;

            EnsureModelLoaded(out model, out loadError);

            if (model == null)
            {
                return new ExecutionRiskPrediction
                {
                    IsAvailable = false,
                    ModelKey = ExpectedModelKey,
                    ModelName = "BuyExecutionRiskShadow",
                    ModelVersion = null,
                    EvaluatorType = "LogisticRegression",
                    FeatureSchemaVersion = null,
                    Reason = string.IsNullOrWhiteSpace(loadError)
                        ? "BUY Shadow model unavailable"
                        : loadError
                };
            }

            if (feature == null)
                return Unavailable(model, "FeatureSnapshotRecord is null");

            double z = model.Intercept;
            List<ExecutionRiskFeatureParameter> parameters = model.Features;

            if (parameters == null || parameters.Count == 0)
                return Unavailable(model, "Model feature list is empty");

            for (int i = 0; i < parameters.Count; i++)
            {
                ExecutionRiskFeatureParameter p = parameters[i];
                if (p == null || string.IsNullOrWhiteSpace(p.Name))
                    return Unavailable(model, "Model contains invalid feature parameter");

                double raw;
                if (!ExecutionRiskFeatureMapper.TryGetValue(feature, p.Name, out raw))
                    return Unavailable(model, "Runtime feature mapping missing: " + p.Name);

                if (double.IsNaN(raw) || double.IsInfinity(raw))
                    raw = p.Median;

                double std = Math.Abs(p.StdDev) < 1e-12 ? 1.0 : p.StdDev;
                double scaled = (raw - p.Mean) / std;
                z += p.Weight * scaled;
            }

            double risk = Sigmoid(z);
            double confidence = Math.Min(1.0, Math.Abs(risk - 0.5) * 2.0);

            return new ExecutionRiskPrediction
            {
                IsAvailable = true,
                RiskProbability = risk,
                Confidence = confidence,
                ModelKey = model.ModelKey,
                ModelName = model.ModelName,
                ModelVersion = model.ModelVersion,
                EvaluatorType = model.EvaluatorType,
                FeatureSchemaVersion = model.FeatureSchemaVersion,
                Reason = "Shadow only; no trading influence"
            };
        }

        private static ExecutionRiskPrediction Unavailable(ExecutionRiskModelDocument model, string reason)
        {
            return new ExecutionRiskPrediction
            {
                IsAvailable = false,
                ModelKey = model == null ? ExpectedModelKey : model.ModelKey,
                ModelName = model == null ? "BuyExecutionRiskShadow" : model.ModelName,
                ModelVersion = model == null ? null : model.ModelVersion,
                EvaluatorType = model == null ? "LogisticRegression" : model.EvaluatorType,
                FeatureSchemaVersion = model == null ? null : model.FeatureSchemaVersion,
                Reason = reason
            };
        }

        private void EnsureModelLoaded(out ExecutionRiskModelDocument model, out string error)
        {
            string path = ResolveModelPath();
            DateTime writeTimeUtc = DateTime.MinValue;

            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    writeTimeUtc = File.GetLastWriteTimeUtc(path);
            }
            catch
            {
                writeTimeUtc = DateTime.MinValue;
            }

            lock (_sync)
            {
                bool sameFile = string.Equals(_loadedPath, path, StringComparison.OrdinalIgnoreCase);
                bool sameVersion = sameFile && _loadedWriteTimeUtc == writeTimeUtc;

                if (!sameVersion)
                    LoadModel_NoLock(path, writeTimeUtc);

                model = _model;
                error = _loadError;
            }
        }

        private void LoadModel_NoLock(string path, DateTime writeTimeUtc)
        {
            _loadedPath = path;
            _loadedWriteTimeUtc = writeTimeUtc;
            _model = null;
            _loadError = null;

            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                _loadError = "BUY Shadow model file not found: " + path;
                return;
            }

            try
            {
                string json = File.ReadAllText(path);
                ExecutionRiskModelDocument model = JsonConvert.DeserializeObject<ExecutionRiskModelDocument>(json);

                ValidateModel(model);
                _model = model;
            }
            catch (Exception ex)
            {
                _loadError = "BUY Shadow model load failed: " + ex.Message;
                _model = null;
            }
        }

        private static void ValidateModel(ExecutionRiskModelDocument model)
        {
            if (model == null)
                throw new InvalidDataException("Model JSON is empty");

            if (!string.Equals(model.ModelKey, ExpectedModelKey, StringComparison.Ordinal))
                throw new InvalidDataException("Unexpected ModelKey: " + model.ModelKey);

            if (!string.Equals(model.Target, ExpectedTarget, StringComparison.Ordinal))
                throw new InvalidDataException("Unexpected target: " + model.Target);

            if (!string.Equals(model.Side, "BUY", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("BUY model required");

            if (model.Features == null || model.Features.Count == 0)
                throw new InvalidDataException("No model features");

            HashSet<string> names = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < model.Features.Count; i++)
            {
                ExecutionRiskFeatureParameter p = model.Features[i];
                if (p == null || string.IsNullOrWhiteSpace(p.Name))
                    throw new InvalidDataException("Invalid feature parameter");

                if (!names.Add(p.Name))
                    throw new InvalidDataException("Duplicate feature: " + p.Name);

                double dummy;
                if (!ExecutionRiskFeatureMapper.TryGetValue(new FeatureSnapshotRecord(), p.Name, out dummy))
                    throw new InvalidDataException("Unsupported runtime feature: " + p.Name);
            }
        }

        private static string ResolveModelPath()
        {
            string env = Environment.GetEnvironmentVariable("MONEYCREATOR_BUY_EXECUTION_RISK_MODEL");
            if (!string.IsNullOrWhiteSpace(env))
                return Path.GetFullPath(env.Trim());

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string primary = Path.Combine(baseDir, "ML", "Models", "BuyExecutionRiskModel.json");
            if (File.Exists(primary))
                return primary;

            return Path.Combine(baseDir, "Models", "ML", "BuyExecutionRiskModel.json");
        }

        private static double Sigmoid(double z)
        {
            if (z >= 0.0)
            {
                double e = Math.Exp(-z);
                return 1.0 / (1.0 + e);
            }

            double ez = Math.Exp(z);
            return ez / (1.0 + ez);
        }
    }
}
