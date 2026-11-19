# Adding a workflow step

Every directory write in Rollcall is implemented as an `IWorkflowStep` so planning, previewing, drift detection, approval gating, retryability, permissions documentation, and audit logging work uniformly.

## Checklist

1. **Implement `WorkflowStepBase<TInput>`** in `src/Rollcall.Infrastructure/Workflow/Steps/`:
   - `StepType`: unique dot-delimited identifier (for example `entra.user.update_attributes`).
   - `System`: `TargetSystem.EntraId`, `TargetSystem.ExchangeOnline`, or `TargetSystem.ActiveDirectory`.
   - `RequiredPermissions`: declare every Microsoft Graph application permission, Entra directory role, or AD right this step uses.
   - `PlanAsync`: read current state (using `$select` on Graph reads), return `StepPlan` with exact `OldValue`/`NewValue`, `IsNoOp`, `Warnings`, and `BlockingErrors`. Never mutate state in `PlanAsync`.
   - `ExecuteAsync`: perform the idempotent write, capture any `UndoDescriptor` needed for restore/reversal, and return plain-language summaries.
2. **Register the step** in `src/Rollcall.Infrastructure/ServiceCollectionExtensions.cs` (`AddSingleton<IWorkflowStep, YourStep>()` or `AddScoped`).
3. **Regenerate the permissions reference** so CI stays green:
   ```bash
   dotnet run --project src/Rollcall.Api -- generate-permissions-doc docs/reference/permissions.md
   ```
4. **Add tests** in `tests/Rollcall.Api.Tests/` using `FakeGraphHandler`, `FakeExchangeSessionFactory`, or `FakeActiveDirectory` covering:
   - Plan preview (including no-op and collision/error cases)
   - Execution and idempotency
   - Drift detection (`NeedsReview`)
   - Audit log entry verification
