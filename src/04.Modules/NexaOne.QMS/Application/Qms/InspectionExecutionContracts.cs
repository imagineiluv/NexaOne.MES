using System.Security.Cryptography;
using System.Text.Json;
using NexaOne.QMS.Domain;

namespace NexaOne.QMS.Application.Qms;

/// <summary>검사 실행의 단일 규격 항목 입력입니다. 규격 판정은 서버의 현재 규격값으로 계산합니다.</summary>
public sealed record InspectionExecutionItemCommand(
    string SpecId,
    decimal? MeasuredValue,
    string? AttributeResult,
    int SampleQuantity,
    int DefectQuantity,
    string? Remark);

/// <summary>v2 검사 실행 생성 명령입니다. 식별자·검사자·확정시각은 서버가 생성합니다.</summary>
public sealed record RecordInspectionExecutionCommand(
    string IdempotencyKey,
    InspectionExecutionType InspectionType,
    InspectionExecutionRelationType RelationType,
    string? ParentInspectionId,
    string LotId,
    string EquipmentId,
    int LotQuantity,
    int SampleQuantity,
    int DefectQuantity,
    string? SamplingPlanRevisionId,
    IReadOnlyList<InspectionExecutionItemCommand> Items,
    string? Remark);

/// <summary>최초 확정과 동일 요청 재생을 구분하는 서비스 결과입니다.</summary>
public sealed record InspectionExecutionOutcome(InspectionExecution Execution, bool IsReplay);

/// <summary>
/// DTO 직렬화 순서나 런타임 문화권에 영향을 받지 않는 SHA-256 요청 지문을 생성합니다.
/// actor도 포함하므로 다른 사용자가 같은 키를 재사용하면 동일 요청으로 간주되지 않습니다.
/// </summary>
/// <summary>
/// Produces stable SHA-256 fingerprints for inspection commands. String values are trimmed,
/// the authenticated actor is part of the fingerprint, and item order is preserved deliberately:
/// reordering items represents a different request because it also determines persisted item
/// sequence. Cancellation uses a separate canonical payload containing operation, inspection,
/// trimmed reason, and actor so a cancellation key cannot be replayed with different semantics.
/// </summary>
public static class InspectionExecutionRequestHasher
{
    public static string Compute(RecordInspectionExecutionCommand command, string actorId)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("inspectionType", command.InspectionType.ToString());
            writer.WriteString("relationType", command.RelationType.ToString());
            WriteString(writer, "parentInspectionId", command.ParentInspectionId);
            writer.WriteString("lotId", Normalize(command.LotId));
            writer.WriteString("equipmentId", Normalize(command.EquipmentId));
            writer.WriteNumber("lotQuantity", command.LotQuantity);
            writer.WriteNumber("sampleQuantity", command.SampleQuantity);
            writer.WriteNumber("defectQuantity", command.DefectQuantity);
            WriteString(writer, "samplingPlanRevisionId", command.SamplingPlanRevisionId);
            WriteString(writer, "remark", command.Remark);
            writer.WriteString("actorId", Normalize(actorId));
            writer.WriteStartArray("items");
            foreach (var item in command.Items ?? [])
            {
                writer.WriteStartObject();
                writer.WriteString("specId", Normalize(item.SpecId));
                if (item.MeasuredValue.HasValue)
                    writer.WriteNumber("measuredValue", item.MeasuredValue.Value);
                else
                    writer.WriteNull("measuredValue");
                WriteString(writer, "attributeResult", item.AttributeResult);
                writer.WriteNumber("sampleQuantity", item.SampleQuantity);
                writer.WriteNumber("defectQuantity", item.DefectQuantity);
                WriteString(writer, "remark", item.Remark);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    public static string ComputeCancellation(
        string inspectionId, string reason, string actorId)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("operation", "Cancel");
            writer.WriteString("inspectionId", Normalize(inspectionId));
            writer.WriteString("reason", Normalize(reason));
            writer.WriteString("actorId", Normalize(actorId));
            writer.WriteEndObject();
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    private static void WriteString(Utf8JsonWriter writer, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) writer.WriteNull(name);
        else writer.WriteString(name, value.Trim());
    }

    private static string Normalize(string? value) => value?.Trim() ?? string.Empty;
}
