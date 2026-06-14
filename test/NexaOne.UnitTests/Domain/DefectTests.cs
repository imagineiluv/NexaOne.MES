using System.Text.Json;
using NexaOne.QMS.Domain;

namespace NexaOne.UnitTests.Domain;

/// <summary>Defect 애그리거트 — 확정 시 도메인 이벤트 발행(ADR-002 QMS 슬라이스).</summary>
public sealed class DefectTests
{
    private static Defect NewDefect(string defectId = "DF-1", string lotId = "LOT-1") =>
        Defect.Create(defectId, lotId, "EQ-1", "DC-SCRATCH", 7, 0.035m, DateTime.UtcNow, "INS-1").Value;

    [Fact]
    public void Confirm_raises_DefectConfirmed_event_with_outbox_envelope()
    {
        var defect = NewDefect("DF-1", "LOT-7");

        defect.Confirm("QA-9").IsSuccess.Should().BeTrue();

        var ev = defect.DomainEvents.OfType<DefectConfirmedDomainEvent>().Should().ContainSingle().Subject;
        ev.EventType.Should().Be("DefectConfirmed");
        ev.Module.Should().Be("QMS");
        ev.AggregateId.Should().Be("LOT-7", "Lot별 순서 보장을 위해 AGGREGATE_ID는 LotId여야 한다");

        using var doc = JsonDocument.Parse(ev.Payload);
        doc.RootElement.GetProperty("DefectId").GetString().Should().Be("DF-1");
        doc.RootElement.GetProperty("DefectClassId").GetString().Should().Be("DC-SCRATCH");
        doc.RootElement.GetProperty("EquipmentId").GetString().Should().Be("EQ-1");
        doc.RootElement.GetProperty("DefectCount").GetInt32().Should().Be(7);
        doc.RootElement.GetProperty("DefectRate").GetDecimal().Should().Be(0.035m);
        doc.RootElement.GetProperty("ConfirmedBy").GetString().Should().Be("QA-9");
        doc.RootElement.TryGetProperty("ConfirmedAt", out _).Should().BeTrue("Payload에 ConfirmedAt가 포함돼야 한다");
    }

    [Fact]
    public void Restore_raises_no_domain_events()
        => Defect.Restore("DF-2", "LOT-1", "EQ-1", "DC-SCRATCH", 7, 0.035m, DateTime.UtcNow, "INS-1",
                null, isConfirmed: true, DateTime.UtcNow, "QA-9", DateTime.UtcNow)
            .DomainEvents.Should().BeEmpty("읽기경로 재구성(Restore)은 도메인 이벤트 발행 대상이 아니다");

    [Fact]
    public void ClearDomainEvents_empties_the_list()
    {
        var defect = NewDefect();
        defect.Confirm("QA-9");
        defect.ClearDomainEvents();
        defect.DomainEvents.Should().BeEmpty("리포가 발행 후 이벤트를 비워야 재발행되지 않는다");
    }
}
