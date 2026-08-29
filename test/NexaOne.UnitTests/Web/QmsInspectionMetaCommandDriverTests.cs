using NexaOne.Web.Services.Api;
using NexaOne.Web.Services.Meta;

namespace NexaOne.UnitTests.Web;

public sealed class QmsInspectionMetaCommandDriverTests
{
    private static readonly MetaCommandExecutionContext Context =
        new("QMS_INSPECTION_REGISTER", "MES");

    [Fact]
    public void Descriptors_are_per_row_mutating_commands()
    {
        var driver = new QmsInspectionMetaCommandDriver(new Mock<IApiClient>().Object);

        driver.Commands.Should().HaveCount(QmsInspectionMetaCommands.All.Count);
        driver.Commands.Should().OnlyContain(command =>
            command.RequiredPermission == "qms:manage"
            && command.ExecutionMode == MetaCommandExecutionMode.PerRow
            && command.Effect == MetaCommandEffect.Mutating);
    }

    [Theory]
    [InlineData(QmsInspectionMetaCommands.RecordIncoming, "Incoming")]
    [InlineData(QmsInspectionMetaCommands.RecordProcess, "Process")]
    [InlineData(QmsInspectionMetaCommands.RecordShipping, "Shipping")]
    public async Task Execute_sends_collection_to_v2_without_client_inspection_id(
        string commandId, string expectedInspectionType)
    {
        RecordInspectionExecutionV2Request? captured = null;
        var api = new Mock<IApiClient>();
        api.Setup(client => client.RecordInspectionExecutionV2Async(
                It.IsAny<RecordInspectionExecutionV2Request>(), It.IsAny<CancellationToken>()))
            .Callback<RecordInspectionExecutionV2Request, CancellationToken>(
                (request, _) => captured = request)
            .ReturnsAsync(() => new InspectionExecutionApiResult(
                ResultDto(captured!), null, 201));
        var driver = new QmsInspectionMetaCommandDriver(api.Object);

        var result = await driver.ExecuteAsync(commandId, ValidParameters(), Context);

        result.Success.Should().BeTrue();
        result.StatusCode.Should().Be(201);
        captured.Should().NotBeNull();
        captured!.InspectionType.Should().Be(expectedInspectionType);
        captured.IdempotencyKey.Should().Be("QMS-KEY-100");
        captured.Items.Should().ContainSingle(x =>
            x.SpecId == "SPEC-100" && x.MeasuredValue == 12.5m);
        typeof(RecordInspectionExecutionV2Request).GetProperty("InspectionId").Should().BeNull();
        api.Verify(client => client.RecordInspectionResultAsync(
            It.IsAny<object>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("idempotencyKey", "멱등키")]
    [InlineData("lotId", "LOT")]
    [InlineData("equipmentId", "설비")]
    public void CanExecute_rejects_each_missing_required_identifier(
        string missingKey, string expectedLabel)
    {
        var driver = new QmsInspectionMetaCommandDriver(new Mock<IApiClient>().Object);
        var parameters = ValidParameters();
        parameters.Remove(missingKey);

        var availability = driver.CanExecute(
            QmsInspectionMetaCommands.RecordIncoming, parameters, Context);

        availability.CanExecute.Should().BeFalse();
        availability.DisabledReason.Should().Contain(expectedLabel);
    }

    [Fact]
    public void CanExecute_rejects_missing_items_collection()
    {
        var driver = new QmsInspectionMetaCommandDriver(new Mock<IApiClient>().Object);
        var parameters = ValidParameters();
        parameters.Remove("items");

        driver.CanExecute(QmsInspectionMetaCommands.RecordProcess, parameters, Context)
            .DisabledReason.Should().Contain("항목");
    }

    [Theory]
    [InlineData(null, null, "중 하나")]
    [InlineData("12.5", "Pass", "중 하나만")]
    [InlineData(null, "Unknown", "Pass/Fail")]
    public void CanExecute_validates_each_collection_item(
        string? measuredValue, string? attributeResult, string reason)
    {
        var driver = new QmsInspectionMetaCommandDriver(new Mock<IApiClient>().Object);
        var parameters = ValidParameters();
        parameters["items"] = new[]
        {
            new Dictionary<string, object?>
            {
                ["specId"] = "SPEC-100",
                ["measuredValue"] = measuredValue,
                ["attributeResult"] = attributeResult,
                ["sampleQuantity"] = 10,
                ["defectQuantity"] = 0
            }
        };

        var availability = driver.CanExecute(
            QmsInspectionMetaCommands.RecordIncoming, parameters, Context);

        availability.CanExecute.Should().BeFalse();
        availability.DisabledReason.Should().Contain(reason);
    }

    [Fact]
    public async Task Execute_rejects_invalid_input_without_calling_v2_api()
    {
        var api = new Mock<IApiClient>();
        var driver = new QmsInspectionMetaCommandDriver(api.Object);
        var parameters = ValidParameters();
        parameters["defectQuantity"] = 11;

        var result = await driver.ExecuteAsync(
            QmsInspectionMetaCommands.RecordIncoming, parameters, Context);

        result.Success.Should().BeFalse();
        result.StatusCode.Should().Be(400);
        api.Verify(client => client.RecordInspectionExecutionV2Async(
            It.IsAny<RecordInspectionExecutionV2Request>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Execute_propagates_idempotency_conflict_status_and_message()
    {
        var api = new Mock<IApiClient>();
        api.Setup(client => client.RecordInspectionExecutionV2Async(
                It.IsAny<RecordInspectionExecutionV2Request>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InspectionExecutionApiResult(
                null, "멱등키가 다른 요청에 이미 사용되었습니다.", 409));
        var driver = new QmsInspectionMetaCommandDriver(api.Object);

        var result = await driver.ExecuteAsync(
            QmsInspectionMetaCommands.RecordProcess, ValidParameters(), Context);

        result.Success.Should().BeFalse();
        result.StatusCode.Should().Be(409);
        result.Error.Should().Contain("멱등키");
    }

    [Fact]
    public void CanExecute_rejects_partial_full_inspection_and_duplicate_specs()
    {
        var driver = new QmsInspectionMetaCommandDriver(new Mock<IApiClient>().Object);
        var partial = ValidParameters();
        partial["sampleQuantity"] = 5;
        partial["items"] = new[]
        {
            new Dictionary<string, object?>
            {
                ["specId"] = "SPEC-100", ["measuredValue"] = 10m,
                ["sampleQuantity"] = 5, ["defectQuantity"] = 0
            }
        };
        driver.CanExecute(QmsInspectionMetaCommands.RecordProcess, partial, Context)
            .DisabledReason.Should().Contain("LOT 전체");

        var duplicate = ValidParameters();
        var first = ((IEnumerable<Dictionary<string, object?>>)duplicate["items"]!).First();
        duplicate["items"] = new[] { first, new Dictionary<string, object?>(first) };
        driver.CanExecute(QmsInspectionMetaCommands.RecordProcess, duplicate, Context)
            .DisabledReason.Should().Contain("중복");
    }

    private static Dictionary<string, object?> ValidParameters()
        => new(StringComparer.Ordinal)
        {
            ["idempotencyKey"] = "QMS-KEY-100",
            ["lotId"] = "LOT-100",
            ["equipmentId"] = "EQ-100",
            ["lotQuantity"] = 10,
            ["sampleQuantity"] = 10,
            ["defectQuantity"] = 0,
            ["items"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["specId"] = "SPEC-100",
                    ["measuredValue"] = 12.5m,
                    ["sampleQuantity"] = 10,
                    ["defectQuantity"] = 0,
                    ["remark"] = "자동 계측"
                }
            },
            ["remark"] = "다항목 확정"
        };

    private static InspectionExecutionV2Dto ResultDto(
        RecordInspectionExecutionV2Request request)
        => new(
            "QMSI-SERVER", request.InspectionType, "Original", "QMSI-SERVER", null,
            request.LotId, request.EquipmentId,
            request.LotQuantity, request.SampleQuantity, request.DefectQuantity,
            request.IdempotencyKey, new string('a', 64), DateTime.UtcNow, "tester",
            true, false, false, request.Remark, null,
            [new("QMSR-SERVER", request.Items[0].SpecId, request.Items[0].MeasuredValue,
                request.Items[0].AttributeResult, request.Items[0].SampleQuantity,
                request.Items[0].DefectQuantity, true, request.Items[0].Remark)],
            [], []);
}
