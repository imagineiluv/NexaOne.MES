namespace NexaOne.API.Services;

public interface IMailTemplateService
{
    /// <summary>Config/Mail/{template}({culture}).xml을 로드해 ${KEY} 플레이스홀더를 치환한다 (§20.10).
    /// 요청 문화권 파일이 없으면 ko-KR로 폴백하고, 그것도 없으면 null을 반환한다.</summary>
    string? Render(string templateName, string culture, IReadOnlyDictionary<string, string> values);
}
