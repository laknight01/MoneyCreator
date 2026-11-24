BUY Execution Risk Shadow model location
========================================

Default runtime model filename:
  BuyExecutionRiskModel.json

The MoneyCreator project copies ML\Models\*.json to the executable output folder.

Alternative:
Set environment variable MONEYCREATOR_BUY_EXECUTION_RISK_MODEL to an absolute model path.

The BUY model is Shadow-only:
- It does not change StableFinalScore.
- It does not block or delay Buy orders.
- It does not affect Trend authority.
- It only writes LocalMlEvaluationLogs for persisted BUY DecisionCandidates.
