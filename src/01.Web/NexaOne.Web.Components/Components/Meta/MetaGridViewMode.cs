namespace NexaOne.Web.Components.Meta;

/// <summary>
/// 관리형 메타 목록의 표시 방식입니다. 동일한 필터·정렬·선택 상태를 유지한 채 표현만 전환합니다.
/// </summary>
public enum MetaGridViewMode
{
    /// <summary>가독성을 우선한 기본 표입니다.</summary>
    StandardTable,

    /// <summary>한 화면에 더 많은 행을 보여 주는 밀집 표입니다.</summary>
    DenseTable,

    /// <summary>주요 식별자와 필드를 카드 단위로 표시합니다.</summary>
    Card,

    /// <summary>표와 선택 행의 상세 정보를 나란히 표시합니다.</summary>
    SplitDetail,
}
