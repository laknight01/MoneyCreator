using System;
using System.Collections.Generic;

namespace MoneyCreator.ML
{
    public sealed class ExecutionRiskModelDocument
    {
        public string ModelKey { get; set; }
        public string ModelName { get; set; }
        public string ModelVersion { get; set; }
        public string EvaluatorType { get; set; }
        public string Target { get; set; }
        public string Side { get; set; }
        public string FeatureSchemaVersion { get; set; }
        public string TrainingStartDate { get; set; }
        public string TrainingEndDate { get; set; }
        public int TrainingRowCount { get; set; }
        public double Intercept { get; set; }
        public List<ExecutionRiskFeatureParameter> Features { get; set; }
        public ExecutionRiskValidationSummary Validation { get; set; }
    }

    public sealed class ExecutionRiskFeatureParameter
    {
        public string Name { get; set; }
        public double Median { get; set; }
        public double Mean { get; set; }
        public double StdDev { get; set; }
        public double Weight { get; set; }
    }

    public sealed class ExecutionRiskValidationSummary
    {
        public int TestDayCount { get; set; }
        public double MeanRocAuc { get; set; }
        public double MedianRocAuc { get; set; }
        public int RocAboveHalfDays { get; set; }
        public double HighRiskExtremeRate { get; set; }
        public double LowRiskExtremeRate { get; set; }
        public double HighRiskReturn60 { get; set; }
        public double LowRiskReturn60 { get; set; }
    }

    public sealed class ExecutionRiskPrediction
    {
        public bool IsAvailable { get; set; }
        public double RiskProbability { get; set; }
        public double Confidence { get; set; }
        public string ModelKey { get; set; }
        public string ModelName { get; set; }
        public string ModelVersion { get; set; }
        public string EvaluatorType { get; set; }
        public string FeatureSchemaVersion { get; set; }
        public string Reason { get; set; }
    }
}
