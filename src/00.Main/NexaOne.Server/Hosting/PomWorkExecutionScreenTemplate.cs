using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;
using NexaOne.Web.Services.Meta;

namespace NexaOne.Server;

/// <summary>
/// MES, Mobile, POP 채널이 함께 사용하는 생산 작업지시 실행 화면 정의를 만든다.
/// 채널과 진입 경로는 <c>SYS_SCREEN_TARGET</c>이 담당하고, 이 템플릿은 세 채널의 조회·입력·명령 계약을
/// 하나의 강타입 <see cref="ScreenDefinition"/>으로 유지한다.
/// </summary>
internal static class PomWorkExecutionScreenTemplate
{
    internal const int TemplateRevision = 5;
    internal const string TemplateRevisionField = "__POM_WORK_EXECUTION_TEMPLATE_REVISION_5";
    private const string LegacyRevision4Field = "__POM_WORK_EXECUTION_TEMPLATE_REVISION_4";
    private const string LegacyRevision3Field = "__POM_WORK_EXECUTION_TEMPLATE_REVISION_3";
    // 과거 DB의 uiId/title을 고정 placeholder로 치환하고 JSON object key를 정렬한 canonical SHA-256.
    // 자동 업그레이드 판정은 frozen fingerprint를 사용하므로 이후 템플릿 빌더 변경과 독립적이다.
    internal const string LegacyRevision1GoldenSha256 =
        "9b9746295a5a938328e5f4b1a7f0864e765471bf46e75dd024aa27a25d028376";
    internal const string LegacyRevision3GoldenSha256 =
        "2f81beb8376bb1b98800a11f279b277cc886eb5b98014a8ef49d3b1deab3e5b1";
    private const string LegacyRevision2Field = "__POM_WORK_EXECUTION_TEMPLATE_REVISION_2";
    internal const string WorkOrderQueryId = "POM.WorkOrderList";
    internal const string ExecutionQueryId = "POM.WorkOrderExecutionList";
    internal const string LotRoutingQueryId = "POM.LotRoutingContextList";
    internal const string RouteExceptionQueryId = "POM.RouteExceptionList";
    internal const string RouteTimelineQueryId = "POM.RouteDeviationTimeline";
    internal const string LotDefectExecutionQueryId = "POM.LotDefectExecutionList";

    private static readonly string[] WorkOrderCommandIds =
    {
        "bridge:pom.work-order.start",
        "bridge:pom.work-order.report",
        "bridge:pom.work-order.hold",
        "bridge:pom.work-order.release-hold",
        "bridge:pom.work-order.complete",
    };

    private static readonly string[] LotCommandIds =
    {
        "bridge:pom.lot.track-in",
        "bridge:pom.lot.track-out",
        "bridge:pom.route.exception.request",
        "bridge:pom.route.exception.approve",
        "bridge:pom.route.exception.reject",
    };

    private static readonly HashSet<string> KnownDefinitionProperties = new(StringComparer.Ordinal)
    {
        "uiId", "title", "fields", "columns", "queryId", "saveQueryId", "layout",
        "refreshIntervalSeconds", "searchFields", "countQueryId", "deleteQueryId", "bulkCommands", "purpose",
        "readRequiredPermission", "saveRequiredPermission", "deleteRequiredPermission",
    };

    /// <summary>채널별 UI ID와 제목을 유지하면서 공통 작업실행 레이아웃을 생성한다.</summary>
    internal static ScreenDefinition Create(string uiId, string title)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uiId);
        ArgumentNullException.ThrowIfNull(title);

        return new ScreenDefinition(
            uiId,
            title,
            new[]
            {
                new FieldDefinition(
                    TemplateRevisionField,
                    $"Managed template revision {TemplateRevision}",
                    ReadOnly: true,
                    Hidden: true),
            },
            Layout: BuildLayout(),
            SearchFields: BuildSearchFields(),
            Purpose: ScreenPurpose.Execute,
            ReadRequiredPermission: "pom:read");
    }

    /// <summary>DB 저장 JSON은 런타임과 Designer가 함께 쓰는 단일 직렬화 계약으로 생성한다.</summary>
    internal static string Serialize(string uiId, string title)
        => ScreenDefinitionJson.Serialize(Create(uiId, title));

    /// <summary>
    /// 이전 개발 시드가 만든 빈 canonical 정의인지 판별한다. 알 수 없는 JSON이나 실제 위젯·쿼리·필드가
    /// 하나라도 있는 정의는 Designer 사용자 작업으로 간주해 보존한다.
    /// </summary>
    internal static bool IsEmptyCanonicalDefinition(string uiId, string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return true;

        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(json);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return false;
        }

        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("uiId", out var uiIdProperty)
            || uiIdProperty.ValueKind != JsonValueKind.String
            || !string.Equals(uiIdProperty.GetString(), uiId, StringComparison.OrdinalIgnoreCase)
            || root.EnumerateObject().Any(property => !KnownDefinitionProperties.Contains(property.Name)))
            return false;

        var definition = ScreenDefinitionJson.Deserialize(json);
        if (definition is null
            || !string.Equals(definition.UiId, uiId, StringComparison.OrdinalIgnoreCase))
            return false;

        // ScreenDefinitionJson은 손상되거나 미래 버전인 layout만 null로 격리한다. 원문에는 layout이 있는데
        // 역직렬화되지 않았다면 빈 정의로 오판하지 않고 사용자 JSON을 그대로 둔다.
        var hasNonNullLayout = root.TryGetProperty("layout", out var layoutProperty)
            && layoutProperty.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
        if (hasNonNullLayout && definition.Layout is null) return false;

        return definition.Fields is not { Count: > 0 }
            && definition.Columns is not { Count: > 0 }
            && string.IsNullOrWhiteSpace(definition.QueryId)
            && string.IsNullOrWhiteSpace(definition.SaveQueryId)
            && definition.RefreshIntervalSeconds is null
            && string.IsNullOrWhiteSpace(definition.CountQueryId)
            && string.IsNullOrWhiteSpace(definition.DeleteQueryId)
            && string.IsNullOrWhiteSpace(definition.ReadRequiredPermission)
            && string.IsNullOrWhiteSpace(definition.SaveRequiredPermission)
            && string.IsNullOrWhiteSpace(definition.DeleteRequiredPermission)
            && definition.SearchFields is not { Count: > 0 }
            && definition.BulkCommands is not { Count: > 0 }
            && definition.Purpose == ScreenPurpose.Auto
            && (definition.Layout is null || IsEmptyContainerTree(definition.Layout));
    }

    /// <summary>
    /// 개발 DB가 자동으로 만든 미수정 canonical 템플릿만 다음 revision으로 올릴 수 있는지 판별합니다.
    /// 현재/이전 revision을 같은 제목으로 다시 직렬화한 JSON과 구조가 완전히 같을 때만 관리형으로 인정하므로,
    /// Designer에서 필드·위젯·권한·새 속성을 하나라도 바꾼 정의는 자동 업그레이드 대상에서 제외됩니다.
    /// </summary>
    internal static bool IsManagedCanonicalDefinition(string uiId, string? json)
    {
        if (IsEmptyCanonicalDefinition(uiId, json)) return true;
        if (string.IsNullOrWhiteSpace(json)) return false;

        ScreenDefinition? existing;
        JsonNode? existingNode;
        try
        {
            existing = ScreenDefinitionJson.Deserialize(json);
            existingNode = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return false;
        }

        if (existing is null || existingNode is null
            || !string.Equals(existing.UiId, uiId, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(existing.Title))
            return false;

        return Matches(existingNode, Serialize(uiId, existing.Title))
            || Matches(existingNode, ScreenDefinitionJson.Serialize(CreateLegacyRevision4(uiId, existing.Title)))
            || MatchesLegacyRevision3Golden(existingNode)
            || Matches(existingNode, ScreenDefinitionJson.Serialize(CreateLegacyRevision2(uiId, existing.Title)))
            || Matches(existingNode, ScreenDefinitionJson.Serialize(CreateLegacyRevision1(uiId, existing.Title)))
            || MatchesLegacyRevision1Golden(existingNode);
    }

    /// <summary>
    /// 정확한 historical revision 1이며 JSON과 DB 제목 모두 UI ID placeholder인지를 판별한다.
    /// 최신 관리 화면에서 사용자가 UI ID를 제목으로 선택한 경우까지 일괄 교체하지 않도록 초기 지문으로 범위를 제한한다.
    /// </summary>
    internal static bool IsHistoricalRevision1PlaceholderDefinition(string uiId, string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;

        ScreenDefinition? existing;
        JsonNode? existingNode;
        try
        {
            existing = ScreenDefinitionJson.Deserialize(json);
            existingNode = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return false;
        }

        if (existing is null || existingNode is null
            || !string.Equals(existing.UiId, uiId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(existing.Title, uiId, StringComparison.OrdinalIgnoreCase))
            return false;

        return MatchesLegacyRevision1Golden(existingNode);
    }

    private static bool Matches(JsonNode existing, string candidateJson)
    {
        try
        {
            var candidate = JsonNode.Parse(candidateJson);
            return candidate is not null && JsonNode.DeepEquals(existing, candidate);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool MatchesLegacyRevision3Golden(JsonNode existing)
        => string.Equals(
            CalculateManagedDefinitionFingerprint(existing),
            LegacyRevision3GoldenSha256,
            StringComparison.Ordinal);

    private static bool MatchesLegacyRevision1Golden(JsonNode existing)
        => string.Equals(
            CalculateManagedDefinitionFingerprint(existing),
            LegacyRevision1GoldenSha256,
            StringComparison.Ordinal);

    /// <summary>화면별 ID와 제목을 제외한 정의 전체의 정렬된 SHA-256 지문을 계산한다.</summary>
    internal static string CalculateManagedDefinitionFingerprint(JsonNode existing)
    {
        if (existing.DeepClone() is not JsonObject normalized) return string.Empty;
        normalized["uiId"] = "__UI_ID__";
        normalized["title"] = "__TITLE__";
        var canonical = Canonicalize(normalized).ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = false,
        });
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static JsonNode Canonicalize(JsonNode node)
        => node switch
        {
            JsonObject obj => new JsonObject(obj
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => KeyValuePair.Create(
                    pair.Key, pair.Value is null ? null : Canonicalize(pair.Value)))),
            JsonArray array => new JsonArray(array
                .Select(item => item is null ? null : Canonicalize(item)).ToArray()),
            _ => node.DeepClone(),
        };

    private static LayoutNode BuildLayout(
        bool includeControlModeEditor = true,
        bool allowExternalReturn = false,
        bool useScopedModels = true,
        bool includeDefectCollection = true,
        bool includeReviewProvenance = true,
        bool includeRoutingScope = true)
        => new SectionNode
        {
            Id = "pom-work-execution-root",
            Children = new LayoutNode[]
            {
                new TextWidget
                {
                    Id = "pom-lot-routing-guidance",
                    Text = "LOT을 스캔해 현재·다음 공정을 확인하세요. Strict는 순서를 강제하고, Flexible은 승인 후 예외를 허용하며, NoControl은 필수 사유와 감사 이력을 남기고 즉시 적용합니다.",
                },
                new RowNode
                {
                    Id = "pom-lot-execution-main-row",
                    Children = new LayoutNode[]
                    {
                        new ColumnNode
                        {
                            Id = "pom-lot-list-column",
                            Span = 7,
                            Children = new LayoutNode[]
                            {
                                new SectionNode
                                {
                                    Id = "pom-lot-list-section",
                                    Title = "LOT 스캔 · 공정 흐름",
                                    Children = new LayoutNode[] { BuildLotRoutingGrid(useScopedModels) },
                                },
                            },
                        },
                        new ColumnNode
                        {
                            Id = "pom-lot-detail-column",
                            Span = 5,
                            Children = new LayoutNode[]
                            {
                                new SectionNode
                                {
                                    Id = "pom-lot-detail-section",
                                    Title = "선택 LOT 실행",
                                    Children = BuildLotDetailChildren(
                                        includeControlModeEditor, useScopedModels, includeDefectCollection),
                                },
                            },
                        },
                    },
                },
                new RowNode
                {
                    Id = "pom-route-exception-row",
                    Children = new LayoutNode[]
                    {
                        new ColumnNode
                        {
                            Id = "pom-route-exception-request-column",
                            Span = 5,
                            Children = new LayoutNode[]
                            {
                                new SectionNode
                                {
                                    Id = "pom-route-exception-request-section",
                                    Title = "라우팅 예외 적용 · 승인 요청",
                                    Children = new LayoutNode[]
                                    {
                                        BuildRouteExceptionRequestForm(allowExternalReturn, useScopedModels),
                                        RouteCommandButton(
                                            "request", "예외 적용 / 승인 요청", LotCommandIds[2],
                                            "선택한 라우팅 편차를 적용하거나 Flexible 승인 요청으로 등록하시겠습니까?",
                                            "pom:routing.request", useScopedModels ? "lot" : null),
                                    },
                                },
                            },
                        },
                        new ColumnNode
                        {
                            Id = "pom-route-exception-review-column",
                            Span = 7,
                            Children = new LayoutNode[]
                            {
                                new SectionNode
                                {
                                    Id = "pom-route-exception-review-section",
                                    Title = "예외 승인 대기 · 처리",
                                    Children = new LayoutNode[]
                                    {
                                        BuildRouteExceptionGrid(useScopedModels, includeReviewProvenance),
                                        BuildRouteExceptionReviewForm(useScopedModels),
                                        BuildRouteExceptionReviewCommands(useScopedModels),
                                    },
                                },
                            },
                        },
                    },
                },
                new SectionNode
                {
                    Id = "pom-route-deviation-timeline-section",
                    Title = "LOT 라우팅 편차 타임라인",
                    Children = BuildRouteAuditChildren(useScopedModels, includeDefectCollection),
                },
                new SectionNode
                {
                    Id = "pom-manual-work-order-section",
                    Title = "라우팅 미연결 작업지시 수동 실행",
                    Children = new LayoutNode[]
                    {
                        new TextWidget
                        {
                            Id = "pom-manual-work-order-warning",
                            Text = includeRoutingScope
                                ? "라우팅 실행 범위가 공정 단위 또는 전체 라우팅인 작업지시는 직접 실행할 수 없습니다. 전체 라우팅 W/O는 한 LOT이 첫 공정부터 마지막 공정까지 순차 실행되며, LOT 스캔 Track-In/Track-Out을 사용합니다."
                                : "라우팅 ID 또는 공정 순번이 연결된 작업지시는 아래 버튼으로 직접 실행할 수 없습니다. LOT 스캔 실행을 사용하세요.",
                        },
                        new RowNode
                        {
                            Id = "pom-work-execution-main-row",
                            Children = new LayoutNode[]
                            {
                                new ColumnNode
                                {
                                    Id = "pom-work-order-list-column",
                                    Span = 7,
                                    Children = new LayoutNode[] { BuildWorkOrderGrid(useScopedModels, includeRoutingScope) },
                                },
                                new ColumnNode
                                {
                                    Id = "pom-work-order-detail-column",
                                    Span = 5,
                                    Children = new LayoutNode[]
                                    {
                                        BuildDetailForm(useScopedModels, includeRoutingScope),
                                        BuildCommandRow(useScopedModels),
                                    },
                                },
                            },
                        },
                    },
                },
                new SectionNode
                {
                    Id = "pom-work-execution-history-section",
                    Title = "작업지시 실행 이력",
                    Children = new LayoutNode[] { BuildExecutionGrid(useScopedModels) },
                },
            },
        };

    private static IReadOnlyList<LayoutNode> BuildRouteAuditChildren(
        bool useScopedModels,
        bool includeDefectExecution)
    {
        var children = new List<LayoutNode> { BuildRouteDeviationTimelineGrid(useScopedModels) };
        if (includeDefectExecution)
        {
            children.Add(new SectionNode
            {
                Id = "pom-lot-defect-execution-section",
                Title = "LOT Track-Out 불량 상세 이력",
                Children = new LayoutNode[] { BuildLotDefectExecutionGrid(useScopedModels) },
            });
        }
        return children;
    }

    /// <summary>LOT 실행과 관리자용 통제 모드 설정을 한 선택 컨텍스트에서 다루도록 구성합니다.</summary>
    private static IReadOnlyList<LayoutNode> BuildLotDetailChildren(
        bool includeControlModeEditor,
        bool useScopedModels,
        bool includeDefectCollection)
    {
        var executionChildren = new List<LayoutNode> { BuildLotExecutionForm(useScopedModels) };
        if (includeDefectCollection)
            executionChildren.Add(BuildLotDefectCollection(useScopedModels));
        executionChildren.Add(BuildLotExecutionCommandRow(useScopedModels));

        if (!includeControlModeEditor)
            return executionChildren;

        executionChildren.Add(
            new SectionNode
            {
                Id = "pom-routing-control-mode-section",
                Title = "라우팅 통제 모드 설정",
                RequiredPermission = "pom:manage",
                Children = new LayoutNode[]
                {
                    BuildRoutingControlModeForm(useScopedModels),
                    RouteCommandButton(
                        "change-control-mode", "통제 모드 변경", PomLotRoutingMetaCommands.ChangeControlMode,
                        "선택한 LOT의 라우팅 통제 모드를 변경하시겠습니까? 변경 사유는 감사 이력에 남습니다.",
                        "pom:manage", useScopedModels ? "lot" : null),
                },
            });
        return executionChildren;
    }

    private static GridWidget BuildLotRoutingGrid(bool useScopedModels)
        => new()
        {
            Id = "pom-lot-routing-grid",
            QueryId = LotRoutingQueryId,
            SelectionScope = useScopedModels ? "lot" : null,
            Columns = new[]
            {
                new GridColumnDefinition("LOT_ID", "LOT ID", Width: 145),
                new GridColumnDefinition("PRODUCT_ID", "품목", Width: 110),
                new GridColumnDefinition("CONTROL_MODE", "통제 모드", Width: 105),
                new GridColumnDefinition("ROUTE_STEPS", "라우팅 순서", Width: 220),
                new GridColumnDefinition("CURRENT_STEP", "현재 순번", Width: 85),
                new GridColumnDefinition("CURRENT_PROCESS_ID", "현재 공정", Width: 115),
                new GridColumnDefinition("NEXT_STEP", "다음 순번", Width: 85),
                new GridColumnDefinition("NEXT_PROCESS_ID", "다음 공정", Width: 115),
                new GridColumnDefinition("PROCESS_STATE", "실행 상태", Width: 90),
                new GridColumnDefinition("LOT_STATE", "LOT 상태", Width: 95),
                new GridColumnDefinition("IS_HOLD", "보류", Width: 70),
                new GridColumnDefinition("IS_IN_REWORK", "재작업", Width: 75),
                new GridColumnDefinition("RETURN_STEP", "복귀 순번", Width: 85),
                new GridColumnDefinition("RETURN_PROCESS_ID", "복귀 공정", Width: 110),
                new GridColumnDefinition("VERSION_NO", "버전", Width: 65),
            },
        };

    private static FormWidget BuildLotExecutionForm(bool useScopedModels)
        => new()
        {
            Id = "pom-lot-execution-form",
            BindingScope = useScopedModels ? "lot" : null,
            Fields = new[]
            {
                InputField("lot-id", "LOT_ID", "LOT ID 스캔", required: true),
                ReadOnlyField("lot-plant-id", "PLANT_ID", "공장", required: true),
                ReadOnlyField("lot-product-id", "PRODUCT_ID", "품목"),
                ReadOnlyField("lot-control-mode", "CONTROL_MODE", "라우팅 통제 모드"),
                ReadOnlyField("lot-current-step", "CURRENT_STEP", "현재 공정 인덱스", FieldType.Number),
                ReadOnlyField("lot-current-process", "CURRENT_PROCESS_ID", "현재 공정"),
                ReadOnlyField("lot-next-step", "NEXT_STEP", "다음 공정 인덱스", FieldType.Number),
                ReadOnlyField("lot-next-process", "NEXT_PROCESS_ID", "다음 공정"),
                ReadOnlyField("lot-process-state", "PROCESS_STATE", "실행 상태"),
                ReadOnlyField("lot-version", "VERSION_NO", "LOT 버전", FieldType.Number, required: true),
                InputField("lot-equipment", "EQUIPMENT_ID", "설비", required: true),
                InputField("lot-qty", "QTY", "Track-Out 수량", FieldType.Number, required: true),
                InputField("lot-carrier", "CARRIER_ID", "캐리어"),
            },
        };

    private static CollectionWidget BuildLotDefectCollection(bool useScopedModels)
        => new()
        {
            Id = "pom-lot-defect-collection",
            CollectionKey = "DEFECTS",
            Label = "Track-Out 불량 내역",
            ItemLabel = "불량",
            BindingScope = useScopedModels ? "lot" : null,
            MinItems = 0,
            MaxItems = 20,
            Fields = new[]
            {
                InputField("lot-defect-code", "DEFECT_CODE", "불량 코드", required: true),
                InputField("lot-defect-qty", "DEFECT_QTY", "불량 수량", FieldType.Number, required: true),
            },
        };

    private static RowNode BuildLotExecutionCommandRow(bool useScopedModels)
        => new()
        {
            Id = "pom-lot-execution-command-row",
            RequiredPermission = "pom:execute",
            Children = new LayoutNode[]
            {
                RouteCommandButton(
                    "track-in", "공정 시작 (Track-In)", LotCommandIds[0],
                    "현재 공정에 LOT을 Track-In 하시겠습니까?", "pom:execute",
                    useScopedModels ? "lot" : null),
                RouteCommandButton(
                    "track-out", "공정 완료 (Track-Out)", LotCommandIds[1],
                    "입력 수량으로 현재 공정을 Track-Out 하시겠습니까?", "pom:execute",
                    useScopedModels ? "lot" : null),
            },
        };

    private static FormWidget BuildRoutingControlModeForm(bool useScopedModels)
        => new()
        {
            Id = "pom-routing-control-mode-form",
            RequiredPermission = "pom:manage",
            BindingScope = useScopedModels ? "lot" : null,
            Fields = new[]
            {
                InputField(
                    "route-control-mode-target", "CONTROL_MODE_TARGET", "변경할 통제 모드",
                    FieldType.Select, required: true,
                    options: new[] { "Strict", "Flexible", "NoControl" }),
                InputField(
                    "route-control-mode-reason", "CONTROL_MODE_REASON", "통제 모드 변경 사유",
                    required: true),
            },
        };

    private static FormWidget BuildRouteExceptionRequestForm(bool allowExternalReturn, bool useScopedModels)
        => new()
        {
            Id = "pom-route-exception-request-form",
            BindingScope = useScopedModels ? "lot" : null,
            Fields = new[]
            {
                InputField("route-exception-type", "DEVIATION_TYPE", "예외 유형", FieldType.Select, required: true,
                    options: allowExternalReturn
                        ? new[] { "Bypass", "Alternative", "SequenceChange", "Rework", "Return" }
                        : new[] { "Bypass", "Alternative", "SequenceChange", "Rework" }),
                InputField("route-exception-target", "TARGET_STEP_INDEX", "목표 순번(0부터)", FieldType.Number, required: true),
                InputField("route-exception-reason", "REASON", "예외 사유", required: true),
            },
        };

    private static GridWidget BuildRouteExceptionGrid(bool useScopedModels, bool includeReviewProvenance)
    {
        var columns = new List<GridColumnDefinition>
        {
            new("EXCEPTION_ID", "예외 ID", Width: 150),
            new("LOT_ID", "LOT ID", Width: 130),
            new("DEVIATION_TYPE", "예외 유형", Width: 110),
            new("FROM_PROCESS_ID", "출발 공정", Width: 105),
            new("TO_PROCESS_ID", "목표 공정", Width: 105),
            new("STATUS", "승인 상태", Width: 95),
            new("REQUESTED_BY", "요청자", Width: 95),
            new("REQUESTED_AT", "요청 시각", Width: 155),
            new("EXPIRES_AT", "만료 시각", Width: 155),
            new("REASON", "요청 사유", Width: 180),
            new("REVIEWED_BY", "승인자", Width: 95),
            new("REVIEW_REASON", "검토 사유", Width: 160),
        };
        if (includeReviewProvenance)
        {
            columns.Add(new GridColumnDefinition("CLIENT_CHANNEL", "요청 채널", Width: 90));
            columns.Add(new GridColumnDefinition("DEVICE_ID", "요청 기기", Width: 110));
            columns.Add(new GridColumnDefinition("REVIEW_CLIENT_CHANNEL", "검토 채널", Width: 90));
            columns.Add(new GridColumnDefinition("REVIEW_DEVICE_ID", "검토 기기", Width: 110));
        }

        return new GridWidget
        {
            Id = "pom-route-exception-grid",
            QueryId = RouteExceptionQueryId,
            RequiredPermission = "pom:read",
            SelectionScope = useScopedModels ? "route-exception" : null,
            Columns = columns,
        };
    }

    private static FormWidget BuildRouteExceptionReviewForm(bool useScopedModels)
        => new()
        {
            Id = "pom-route-exception-review-form",
            BindingScope = useScopedModels ? "route-exception" : null,
            Fields = new[]
            {
                ReadOnlyField("route-review-id", "EXCEPTION_ID", "선택 예외 ID"),
                InputField("route-review-reason", "REVIEW_REASON", "승인·반려 사유"),
            },
        };

    private static RowNode BuildRouteExceptionReviewCommands(bool useScopedModels)
        => new()
        {
            Id = "pom-route-exception-review-command-row",
            Children = new LayoutNode[]
            {
                RouteCommandButton(
                    "approve", "예외 승인", LotCommandIds[3],
                    "선택한 라우팅 예외를 승인하시겠습니까?", "pom:routing.approve",
                    useScopedModels ? "route-exception" : null),
                RouteCommandButton(
                    "reject", "예외 반려", LotCommandIds[4],
                    "선택한 라우팅 예외를 반려하시겠습니까?", "pom:routing.approve",
                    useScopedModels ? "route-exception" : null),
                RouteCommandButton(
                    "apply-approved", "승인 예외 적용", PomLotRoutingMetaCommands.ApplyDeviation,
                    "승인된 라우팅 예외를 LOT에 한 번 적용하시겠습니까?", "pom:routing.request",
                    useScopedModels ? "route-exception" : null),
            },
        };

    private static GridWidget BuildRouteDeviationTimelineGrid(bool useScopedModels)
        => new()
        {
            Id = "pom-route-deviation-timeline-grid",
            QueryId = RouteTimelineQueryId,
            SelectionDisabled = useScopedModels,
            Columns = new[]
            {
                new GridColumnDefinition("CREATED_AT", "발생 시각", Width: 155),
                new GridColumnDefinition("LOT_ID", "LOT ID", Width: 130),
                new GridColumnDefinition("ACTION", "편차 작업", Width: 125),
                new GridColumnDefinition("CONTROL_MODE", "통제 모드", Width: 105),
                new GridColumnDefinition("FROM_PROCESS_ID", "출발 공정", Width: 110),
                new GridColumnDefinition("TO_PROCESS_ID", "목표 공정", Width: 110),
                new GridColumnDefinition("ROUTE_EXCEPTION_ID", "승인 예외 ID", Width: 150),
                new GridColumnDefinition("CREATED_BY", "작업자", Width: 95),
                new GridColumnDefinition("CLIENT_CHANNEL", "채널", Width: 85),
                new GridColumnDefinition("DEVICE_ID", "기기", Width: 105),
                new GridColumnDefinition("REASON", "사유", Width: 200),
            },
        };

    private static GridWidget BuildLotDefectExecutionGrid(bool useScopedModels)
        => new()
        {
            Id = "pom-lot-defect-execution-grid",
            QueryId = LotDefectExecutionQueryId,
            SelectionDisabled = useScopedModels,
            Columns = new[]
            {
                new GridColumnDefinition("OCCURRED_AT", "발생 시각", Width: 155),
                new GridColumnDefinition("LOT_ID", "LOT ID", Width: 130),
                new GridColumnDefinition("EXECUTION_ID", "실행 ID", Width: 150),
                new GridColumnDefinition("PROCESS_ID", "공정", Width: 110),
                new GridColumnDefinition("DEFECT_CODE", "불량 코드", Width: 110),
                new GridColumnDefinition("DEFECT_QTY", "불량 수량", Width: 100),
                new GridColumnDefinition("EXECUTION_USER", "작업자", Width: 100),
                new GridColumnDefinition("CLIENT_CHANNEL", "채널", Width: 85),
                new GridColumnDefinition("DEVICE_ID", "기기", Width: 110),
            },
        };

    private static GridWidget BuildWorkOrderGrid(bool useScopedModels, bool includeRoutingScope = true)
    {
        var columns = new List<GridColumnDefinition>
        {
            new("WORK_ORDER_ID", "작업지시 ID", Width: 150),
            new("PRODUCTION_ORDER_ID", "생산관리오더 ID", Width: 165),
            new("PRODUCT_ID", "품목", Width: 120),
            new("ROUTING_ID", "라우팅", Width: 125),
            new("ROUTING_STEP_NO", "공정 순번", Width: 90),
            new("PROCESS_ID", "공정", Width: 110),
            new("WORK_CENTER_ID", "작업장", Width: 110),
            new("EQUIPMENT_ID", "설비", Width: 110),
            new("OWNER_ID", "담당자", Width: 100),
            new("PLAN_QTY", "계획수량", Width: 100),
            new("COMPLETE_QTY", "양품 누계", Width: 100),
            new("SCRAP_QTY", "불량 누계", Width: 100),
            new("STATUS", "상태", Width: 95),
            new("IS_HOLD", "보류", Width: 75),
            new("VERSION_NO", "버전", Width: 70),
        };
        if (includeRoutingScope)
            columns.Insert(3, new GridColumnDefinition("ROUTING_SCOPE", "라우팅 실행 범위", Width: 135));

        return new GridWidget
        {
            Id = "pom-work-order-list-grid",
            QueryId = WorkOrderQueryId,
            SelectionScope = useScopedModels ? "work-order" : null,
            Columns = columns,
        };
    }

    private static FormWidget BuildDetailForm(bool useScopedModels, bool includeRoutingScope = true)
    {
        var fields = new List<FieldWidget>
        {
            ReadOnlyField("work-order-id", "WORK_ORDER_ID", "작업지시 ID", required: true),
            ReadOnlyField("production-order-id", "PRODUCTION_ORDER_ID", "생산관리오더 ID"),
            ReadOnlyField("product-id", "PRODUCT_ID", "품목"),
            ReadOnlyField("routing-id", "ROUTING_ID", "라우팅"),
            ReadOnlyField("routing-step-no", "ROUTING_STEP_NO", "공정 순번", FieldType.Number),
            ReadOnlyField("process-id", "PROCESS_ID", "공정"),
            ReadOnlyField("work-center-id", "WORK_CENTER_ID", "작업장"),
            ReadOnlyField("equipment-id", "EQUIPMENT_ID", "설비"),
            ReadOnlyField("owner-id", "OWNER_ID", "담당자"),
            ReadOnlyField("status", "STATUS", "상태"),
            ReadOnlyField("version-no", "VERSION_NO", "버전", FieldType.Number, required: true),
            ReadOnlyField("complete-qty", "COMPLETE_QTY", "현재 양품 누계", FieldType.Number),
            ReadOnlyField("scrap-qty", "SCRAP_QTY", "현재 불량 누계", FieldType.Number),
            InputField("good-qty", "goodQty", "양품 누계", FieldType.Number),
            InputField("defect-qty", "defectQty", "불량 누계", FieldType.Number),
            InputField("remark", "remark", "비고"),
        };
        if (includeRoutingScope)
            fields.Insert(3, ReadOnlyField("routing-scope", "ROUTING_SCOPE", "라우팅 실행 범위"));

        return new FormWidget
        {
            Id = "pom-work-order-detail-form",
            BindingScope = useScopedModels ? "work-order" : null,
            Fields = fields,
        };
    }

    private static RowNode BuildCommandRow(bool useScopedModels)
        => new()
        {
            Id = "pom-work-order-command-row",
            RequiredPermission = "pom:execute",
            Children = new LayoutNode[]
            {
                CommandButton("start", "작업 시작", WorkOrderCommandIds[0], "선택한 작업지시를 시작하시겠습니까?", useScopedModels ? "work-order" : null),
                CommandButton("report", "실적 보고", WorkOrderCommandIds[1], "입력한 양품·불량 누계를 보고하시겠습니까?", useScopedModels ? "work-order" : null),
                CommandButton("hold", "작업 보류", WorkOrderCommandIds[2], "선택한 작업지시를 보류하시겠습니까?", useScopedModels ? "work-order" : null),
                CommandButton("release-hold", "보류 해제", WorkOrderCommandIds[3], "선택한 작업지시의 보류를 해제하시겠습니까?", useScopedModels ? "work-order" : null),
                CommandButton("complete", "작업 완료", WorkOrderCommandIds[4], "최종 누계를 확정하고 작업지시를 완료하시겠습니까?", useScopedModels ? "work-order" : null),
            },
        };

    private static GridWidget BuildExecutionGrid(bool useScopedModels)
        => new()
        {
            Id = "pom-work-order-execution-grid",
            QueryId = ExecutionQueryId,
            SelectionDisabled = useScopedModels,
            Columns = new[]
            {
                new GridColumnDefinition("OCCURRED_AT", "발생시각", Width: 165),
                new GridColumnDefinition("WORK_ORDER_ID", "작업지시 ID", Width: 150),
                new GridColumnDefinition("ACTION", "작업", Width: 100),
                new GridColumnDefinition("FROM_STATUS", "이전 상태", Width: 100),
                new GridColumnDefinition("TO_STATUS", "변경 상태", Width: 100),
                new GridColumnDefinition("GOOD_QTY", "양품 누계", Width: 100),
                new GridColumnDefinition("DEFECT_QTY", "불량 누계", Width: 100),
                new GridColumnDefinition("USER_ID", "작업자", Width: 105),
                new GridColumnDefinition("EQUIPMENT_ID", "설비", Width: 110),
                new GridColumnDefinition("CLIENT_CHANNEL", "채널", Width: 85),
                new GridColumnDefinition("DEVICE_ID", "기기", Width: 110),
                new GridColumnDefinition("REMARK", "비고", Width: 180),
            },
        };

    private static IReadOnlyList<FieldDefinition> BuildSearchFields(
        bool allowExternalReturn = false,
        bool includeDefectCode = true,
        bool includeRoutingScope = true)
    {
        var fields = new List<FieldDefinition>
        {
            new FieldDefinition("plantId", "공장"),
            new FieldDefinition("lotId", "LOT ID 스캔 또는 입력"),
            new FieldDefinition("controlMode", "라우팅 통제 모드", FieldType.Select,
                Options: new[] { "Strict", "Flexible", "NoControl" }),
            new FieldDefinition("deviationType", "라우팅 예외 유형", FieldType.Select,
                Options: allowExternalReturn
                    ? new[] { "Bypass", "Alternative", "SequenceChange", "Rework", "Return" }
                    : new[] { "Bypass", "Alternative", "SequenceChange", "Rework" }),
            new FieldDefinition("exceptionStatus", "예외 승인 상태", FieldType.Select,
                Options: new[] { "Requested", "Approved", "Rejected", "Applied", "Expired" }),
            new FieldDefinition("productionOrderId", "생산관리오더 ID"),
            new FieldDefinition("workOrderId", "작업지시 ID"),
            new FieldDefinition("processId", "공정"),
            new FieldDefinition("equipmentId", "설비"),
            new FieldDefinition("ownerId", "담당자"),
            new FieldDefinition("status", "상태", FieldType.Select,
                Options: new[] { "Created", "Released", "Started", "Completed", "Cancelled" }),
            new FieldDefinition("action", "실행 작업", FieldType.Select,
                Options: new[] { "Release", "Start", "Report", "Hold", "ReleaseHold", "Complete", "Cancel" }),
        };
        if (includeRoutingScope)
            fields.Insert(fields.FindIndex(field => field.Key == "processId"),
                new FieldDefinition("routingScope", "라우팅 실행 범위", FieldType.Select,
                    Options: new[] { "Unbound", "Operation", "SerialRoute" }));
        if (includeDefectCode)
            fields.Insert(fields.FindIndex(field => field.Key == "equipmentId"),
                new FieldDefinition("defectCode", "불량 코드"));
        return fields;
    }

    private static FieldWidget ReadOnlyField(
        string id,
        string key,
        string label,
        FieldType type = FieldType.Text,
        bool required = false)
        => new()
        {
            Id = $"pom-work-order-field-{id}",
            FieldKey = key,
            Field = new FieldDefinition(key, label, type, required, ReadOnly: true),
        };

    private static FieldWidget InputField(
        string id,
        string key,
        string label,
        FieldType type = FieldType.Text,
        bool required = false,
        IReadOnlyList<string>? options = null)
        => new()
        {
            Id = $"pom-work-order-field-{id}",
            FieldKey = key,
            Field = new FieldDefinition(key, label, type, required, Options: options),
        };

    private static ButtonWidget CommandButton(
        string id,
        string label,
        string command,
        string confirmMessage,
        string? bindingScope = null)
        => new()
        {
            Id = $"pom-work-order-command-{id}",
            RequiredPermission = "pom:execute",
            Label = label,
            Command = command,
            ConfirmMessage = confirmMessage,
            BindingScope = bindingScope,
        };

    private static ButtonWidget RouteCommandButton(
        string id,
        string label,
        string command,
        string confirmMessage,
        string requiredPermission,
        string? bindingScope = null)
        => new()
        {
            Id = $"pom-route-command-{id}",
            RequiredPermission = requiredPermission,
            Label = label,
            Command = command,
            ConfirmMessage = confirmMessage,
            BindingScope = bindingScope,
        };

    /// <summary>
    /// 라우팅 실행 범위가 화면 계약에 추가되기 직전의 revision 4 canonical 정의입니다.
    /// 구조가 그대로인 자동 시드 화면만 revision 5로 올리고 Designer 사용자 변경은 보존합니다.
    /// </summary>
    internal static ScreenDefinition CreateLegacyRevision4(string uiId, string title)
        => new(
            uiId,
            title,
            new[]
            {
                new FieldDefinition(
                    LegacyRevision4Field,
                    "Managed template revision 4",
                    ReadOnly: true,
                    Hidden: true),
            },
            Layout: BuildLayout(includeRoutingScope: false),
            SearchFields: BuildSearchFields(includeRoutingScope: false),
            Purpose: ScreenPurpose.Execute,
            ReadRequiredPermission: "pom:read");

    /// <summary>
    /// revision 2 canonical을 정확히 재현합니다. 이 버전은 통제 모드 변경 UI가 없고 Return을 외부 편차로
    /// 노출했으므로, 구조가 그대로인 자동 시드 화면에 한해서 revision 3으로 안전하게 올립니다.
    /// </summary>
    internal static ScreenDefinition CreateLegacyRevision2(string uiId, string title)
        => new(
            uiId,
            title,
            new[]
            {
                new FieldDefinition(
                    LegacyRevision2Field,
                    "Managed template revision 2",
                    ReadOnly: true,
                    Hidden: true),
            },
            Layout: BuildLayout(
                includeControlModeEditor: false,
                allowExternalReturn: true,
                useScopedModels: false,
                includeDefectCollection: false,
                includeReviewProvenance: false,
                includeRoutingScope: false),
            SearchFields: BuildSearchFields(
                allowExternalReturn: true, includeDefectCode: false, includeRoutingScope: false),
            Purpose: ScreenPurpose.Execute,
            ReadRequiredPermission: "pom:read");

    /// <summary>
    /// Scope 분리와 Track-Out 불량 반복 입력을 도입하기 직전의 revision 3 canonical 정의입니다.
    /// 이미 배포된 자동 생성 화면만 revision 4로 안전하게 올리고 Designer 사용자 변경은 보존합니다.
    /// </summary>
    internal static ScreenDefinition CreateLegacyRevision3(string uiId, string title)
        => new(
            uiId,
            title,
            new[]
            {
                new FieldDefinition(
                    LegacyRevision3Field,
                    "Managed template revision 3",
                    ReadOnly: true,
                    Hidden: true),
            },
            Layout: BuildLayout(
                includeControlModeEditor: true,
                allowExternalReturn: false,
                useScopedModels: false,
                includeDefectCollection: false,
                includeReviewProvenance: false,
                includeRoutingScope: false),
            SearchFields: BuildSearchFields(includeDefectCode: false, includeRoutingScope: false),
            Purpose: ScreenPurpose.Execute,
            ReadRequiredPermission: "pom:read");

    /// <summary>2026-07-14에 자동 시드된 최초 canonical 구조를 정확히 재현해 사용자 수정 여부를 판별합니다.</summary>
    internal static ScreenDefinition CreateLegacyRevision1(string uiId, string title)
        => new(
            uiId,
            title,
            Array.Empty<FieldDefinition>(),
            Layout: BuildLegacyRevision1Layout(),
            SearchFields: BuildLegacyRevision1SearchFields());

    private static LayoutNode BuildLegacyRevision1Layout()
        => new SectionNode
        {
            Id = "pom-work-execution-root",
            Children = new LayoutNode[]
            {
                new RowNode
                {
                    Id = "pom-work-execution-main-row",
                    Children = new LayoutNode[]
                    {
                        new ColumnNode
                        {
                            Id = "pom-work-order-list-column",
                            Span = 7,
                            Children = new LayoutNode[]
                            {
                                new SectionNode
                                {
                                    Id = "pom-work-order-list-section",
                                    Title = "작업지시 목록",
                                    Children = new LayoutNode[] { BuildLegacyRevision1WorkOrderGrid() },
                                },
                            },
                        },
                        new ColumnNode
                        {
                            Id = "pom-work-order-detail-column",
                            Span = 5,
                            Children = new LayoutNode[]
                            {
                                new SectionNode
                                {
                                    Id = "pom-work-order-detail-section",
                                    Title = "선택 작업지시 상세",
                                    Children = new LayoutNode[]
                                    {
                                        BuildLegacyRevision1DetailForm(),
                                        BuildCommandRow(useScopedModels: false),
                                    },
                                },
                            },
                        },
                    },
                },
                new SectionNode
                {
                    Id = "pom-work-execution-history-section",
                    Title = "작업 실행 이력",
                    Children = new LayoutNode[] { BuildExecutionGrid(useScopedModels: false) },
                },
            },
        };

    private static GridWidget BuildLegacyRevision1WorkOrderGrid()
        => new()
        {
            Id = "pom-work-order-list-grid",
            QueryId = WorkOrderQueryId,
            Columns = new[]
            {
                new GridColumnDefinition("WORK_ORDER_ID", "작업지시 ID", Width: 150),
                new GridColumnDefinition("PRODUCTION_ORDER_ID", "생산관리오더 ID", Width: 165),
                new GridColumnDefinition("PRODUCT_ID", "품목", Width: 120),
                new GridColumnDefinition("PROCESS_ID", "공정", Width: 110),
                new GridColumnDefinition("WORK_CENTER_ID", "작업장", Width: 110),
                new GridColumnDefinition("EQUIPMENT_ID", "설비", Width: 110),
                new GridColumnDefinition("OWNER_ID", "담당자", Width: 100),
                new GridColumnDefinition("PLAN_QTY", "계획수량", Width: 100),
                new GridColumnDefinition("COMPLETE_QTY", "양품 누계", Width: 100),
                new GridColumnDefinition("SCRAP_QTY", "불량 누계", Width: 100),
                new GridColumnDefinition("STATUS", "상태", Width: 95),
                new GridColumnDefinition("IS_HOLD", "보류", Width: 75),
                new GridColumnDefinition("VERSION_NO", "버전", Width: 70),
            },
        };

    private static FormWidget BuildLegacyRevision1DetailForm()
        => new()
        {
            Id = "pom-work-order-detail-form",
            Fields = new[]
            {
                ReadOnlyField("work-order-id", "WORK_ORDER_ID", "작업지시 ID", required: true),
                ReadOnlyField("production-order-id", "PRODUCTION_ORDER_ID", "생산관리오더 ID"),
                ReadOnlyField("product-id", "PRODUCT_ID", "품목"),
                ReadOnlyField("process-id", "PROCESS_ID", "공정"),
                ReadOnlyField("work-center-id", "WORK_CENTER_ID", "작업장"),
                ReadOnlyField("equipment-id", "EQUIPMENT_ID", "설비"),
                ReadOnlyField("owner-id", "OWNER_ID", "담당자"),
                ReadOnlyField("status", "STATUS", "상태"),
                ReadOnlyField("version-no", "VERSION_NO", "버전", FieldType.Number, required: true),
                ReadOnlyField("complete-qty", "COMPLETE_QTY", "현재 양품 누계", FieldType.Number),
                ReadOnlyField("scrap-qty", "SCRAP_QTY", "현재 불량 누계", FieldType.Number),
                InputField("good-qty", "goodQty", "양품 누계", FieldType.Number),
                InputField("defect-qty", "defectQty", "불량 누계", FieldType.Number),
                InputField("remark", "remark", "비고"),
            },
        };

    private static IReadOnlyList<FieldDefinition> BuildLegacyRevision1SearchFields()
        => new[]
        {
            new FieldDefinition("plantId", "공장"),
            new FieldDefinition("productionOrderId", "생산관리오더 ID"),
            new FieldDefinition("workOrderId", "작업지시 ID"),
            new FieldDefinition("processId", "공정"),
            new FieldDefinition("equipmentId", "설비"),
            new FieldDefinition("ownerId", "담당자"),
            new FieldDefinition("status", "상태", FieldType.Select,
                Options: new[] { "Created", "Released", "Started", "Completed", "Cancelled" }),
            new FieldDefinition("action", "실행 작업", FieldType.Select,
                Options: new[] { "Release", "Start", "Report", "Hold", "ReleaseHold", "Complete", "Cancel" }),
        };

    private static bool IsEmptyContainerTree(LayoutNode node)
        => node switch
        {
            SectionNode section => ChildrenAreEmpty(section.Children),
            RowNode row => ChildrenAreEmpty(row.Children),
            ColumnNode column => ChildrenAreEmpty(column.Children),
            _ => false,
        };

    private static bool ChildrenAreEmpty(IReadOnlyList<LayoutNode>? children)
        => children is not { Count: > 0 } || children.All(IsEmptyContainerTree);
}
