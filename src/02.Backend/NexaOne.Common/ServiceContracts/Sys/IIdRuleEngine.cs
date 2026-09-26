namespace NexaOne.ServiceContracts.Sys;

/// <summary>
/// SYS가 소유하는 범용 ID 채번 계약입니다. COM_ID_RULE 마스터의 PREFIX·SEQ_LENGTH·RESET_CYCLE을 해석해
/// 중복 없는 다음 ID를 원자적으로 발급합니다. 소비 모듈은 COM_ID_RULE 물리 스키마를 직접 읽거나 갱신하지 않습니다.
/// </summary>
public interface IIdRuleEngine : INexaModuleBridge
{
    /// <summary>
    /// 규칙 ID로 다음 ID를 발급한다. 리셋 주기(Daily/Monthly/Yearly) 경계가 지나면 시퀀스를 1부터
    /// 재시작하므로, 기간 리셋 규칙은 PREFIX에 날짜를 포함시켜야 최종 ID의 유일성이 유지된다.
    /// </summary>
    Task<string> NextIdAsync(string ruleId, CancellationToken ct = default);
}
