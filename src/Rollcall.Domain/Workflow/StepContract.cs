// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Rollcall.Contracts;
using Rollcall.Domain.Entities;

namespace Rollcall.Domain.Workflow;

[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "Matches the step contract in implementation-plan.md section 4.4.")]
public sealed record RequiredPermission(
    string Api,
    string Permission,
    string? DirectoryRole,
    string Feature,
    string Description);

public sealed record StepPlan(
    TargetSystem TargetSystem,
    string Area,
    StepOperation Operation,
    string Description,
    string ObjectName,
    string? OldValue = null,
    string? NewValue = null,
    bool IsNoOp = false,
    string? NoOpReason = null,
    bool RequiresApproval = false,
    IReadOnlyList<string>? Warnings = null,
    string? Fingerprint = null);

public sealed record UndoDescriptor(
    string StepKind,
    string InputJson,
    string Description);

public sealed record PlanContext(
    string ActorUpn,
    IReadOnlyList<PortalRole> ActorRoles,
    IReadOnlyDictionary<string, bool> EnabledFeatures,
    string DefaultUsageLocation,
    string TenantTimeZone,
    IReadOnlyList<string> BreakGlassAccountIds,
    IReadOnlyList<string> BreakGlassGroupIds,
    CancellationToken CancellationToken = default);

public sealed record StepContext(
    Guid RunId,
    Guid StepId,
    string ActorUpn,
    string CorrelationId,
    int Attempt,
    TimeProvider Clock,
    CancellationToken CancellationToken = default);

public sealed class StepExecutionException : Exception
{
    public StepExecutionException(
        string plainLanguageMessage,
        string rawError,
        bool isTransient = false,
        TimeSpan? retryAfter = null,
        string? graphRequestId = null,
        string? graphClientRequestId = null,
        bool waitingForAgent = false,
        Exception? innerException = null)
        : base(plainLanguageMessage, innerException)
    {
        PlainLanguageMessage = plainLanguageMessage;
        RawError = rawError;
        IsTransient = isTransient;
        RetryAfter = retryAfter;
        GraphRequestId = graphRequestId;
        GraphClientRequestId = graphClientRequestId;
        WaitingForAgent = waitingForAgent;
    }

    public string PlainLanguageMessage { get; }
    public string RawError { get; }
    public bool IsTransient { get; }
    public TimeSpan? RetryAfter { get; }
    public string? GraphRequestId { get; }
    public string? GraphClientRequestId { get; }
    public bool WaitingForAgent { get; }
}

public interface IWorkflowStep
{
    string Kind { get; }
    IReadOnlyList<RequiredPermission> Permissions { get; }
    Task<StepPlan> PlanUntypedAsync(string inputJson, PlanContext ctx);
    Task<StepApplyOutcome> ApplyUntypedAsync(string inputJson, StepContext ctx);
    Task<bool> VerifyUntypedAsync(string inputJson, string resultJson, StepContext ctx);
}

public sealed record StepApplyOutcome(
    string ResultJson,
    UndoDescriptor? Undo,
    string? EphemeralCredentialKind = null,
    string? EphemeralCredentialValue = null,
    DateTimeOffset? EphemeralCredentialExpiresAt = null);

public interface IWorkflowStep<TInput, TResult> : IWorkflowStep
{
    Task<StepPlan> PlanAsync(TInput input, PlanContext ctx);
    Task<TResult> ApplyAsync(TInput input, StepContext ctx);
    Task<bool> VerifyAsync(TInput input, TResult result, StepContext ctx);
    UndoDescriptor? DescribeUndo(TInput input, TResult result);
}

public abstract class WorkflowStepBase<TInput, TResult> : IWorkflowStep<TInput, TResult>
{
    protected static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public abstract string Kind { get; }
    public abstract IReadOnlyList<RequiredPermission> Permissions { get; }
    public abstract Task<StepPlan> PlanAsync(TInput input, PlanContext ctx);
    public abstract Task<TResult> ApplyAsync(TInput input, StepContext ctx);
    public abstract Task<bool> VerifyAsync(TInput input, TResult result, StepContext ctx);
    public virtual UndoDescriptor? DescribeUndo(TInput input, TResult result) => null;

    protected virtual (string? Kind, string? Value, DateTimeOffset? ExpiresAt) ExtractEphemeralCredential(TResult result) =>
        (null, null, null);

    protected virtual string SanitizeResultForPersistence(TResult result) =>
        JsonSerializer.Serialize(result, JsonOptions);

    public async Task<StepPlan> PlanUntypedAsync(string inputJson, PlanContext ctx)
    {
        var input = DeserializeInput(inputJson);
        return await PlanAsync(input, ctx);
    }

    public async Task<StepApplyOutcome> ApplyUntypedAsync(string inputJson, StepContext ctx)
    {
        var input = DeserializeInput(inputJson);
        var result = await ApplyAsync(input, ctx);
        var undo = DescribeUndo(input, result);
        var (credKind, credVal, credExp) = ExtractEphemeralCredential(result);
        var persistedResult = SanitizeResultForPersistence(result);
        return new StepApplyOutcome(persistedResult, undo, credKind, credVal, credExp);
    }

    public async Task<bool> VerifyUntypedAsync(string inputJson, string resultJson, StepContext ctx)
    {
        var input = DeserializeInput(inputJson);
        var result = JsonSerializer.Deserialize<TResult>(resultJson, JsonOptions)!;
        return await VerifyAsync(input, result, ctx);
    }

    private static TInput DeserializeInput(string inputJson) =>
        JsonSerializer.Deserialize<TInput>(inputJson, JsonOptions)
            ?? throw new InvalidOperationException($"Step input for {typeof(TInput).Name} could not be deserialized.");
}

public static class PlanHasher
{
    private static readonly JsonSerializerOptions CanonicalOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public static string ComputeHash(IReadOnlyList<StepPlanDto> steps)
    {
        var canonical = JsonSerializer.Serialize(
            steps.Select(s => new
            {
                s.Sequence,
                s.StepKind,
                s.TargetSystem,
                s.Area,
                s.Operation,
                s.ObjectName,
                s.OldValue,
                s.NewValue,
                s.IsNoOp,
                s.RequiresApproval,
            }),
            CanonicalOptions);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}

public static class AuditHashChain
{
    public const string GenesisHash = "0000000000000000000000000000000000000000000000000000000000000000";

    public static string ComputeHash(AuditEventEntity entry)
    {
        var canonicalDetails = CanonicalizeJson(entry.DetailsJson);
        var payload = string.Create(
            CultureInfo.InvariantCulture,
            $"{entry.PrevHash}|{entry.OccurredAt.ToUniversalTime():O}|{entry.ActorUpn}|{entry.Action}|{entry.Target}|{entry.Outcome}|{entry.RunId?.ToString("D") ?? ""}|{entry.Summary ?? ""}|{canonicalDetails}");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    private static string CanonicalizeJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return "{}";
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = false }))
            {
                WriteCanonicalElement(writer, doc.RootElement);
            }

            return Encoding.UTF8.GetString(ms.ToArray());
        }
        catch (JsonException)
        {
            return json;
        }
    }

    private static void WriteCanonicalElement(Utf8JsonWriter writer, JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var prop in el.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(prop.Name);
                    WriteCanonicalElement(writer, prop.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in el.EnumerateArray())
                {
                    WriteCanonicalElement(writer, item);
                }

                writer.WriteEndArray();
                break;
            default:
                el.WriteTo(writer);
                break;
        }
    }

    public static AuditVerificationResultDto Verify(IReadOnlyList<AuditEventEntity> orderedEvents)
    {
        var expectedPrev = GenesisHash;
        for (var i = 0; i < orderedEvents.Count; i++)
        {
            var e = orderedEvents[i];
            if (!string.Equals(e.PrevHash, expectedPrev, StringComparison.Ordinal))
            {
                return new AuditVerificationResultDto(
                    false,
                    i + 1,
                    e.Id,
                    $"Hash chain broken at audit event {e.Id}: previous hash does not match.");
            }

            var recomputed = ComputeHash(e);
            if (!string.Equals(e.Hash, recomputed, StringComparison.Ordinal))
            {
                return new AuditVerificationResultDto(
                    false,
                    i + 1,
                    e.Id,
                    $"Hash mismatch at audit event {e.Id}: row contents were modified.");
            }

            expectedPrev = e.Hash;
        }

        return new AuditVerificationResultDto(
            true,
            orderedEvents.Count,
            null,
            orderedEvents.Count == 0
                ? "Audit log is empty and intact."
                : $"Verified {orderedEvents.Count} audit events; hash chain is intact.");
    }
}
