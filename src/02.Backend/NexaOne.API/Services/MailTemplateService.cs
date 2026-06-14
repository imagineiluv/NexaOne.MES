namespace NexaOne.API.Services;

/// <summary>현행 레거시 시스템의 Config/Mail/*.xml 템플릿 구조를 그대로 따른다 (§20.10).
/// 템플릿은 HTML 본문이며 플레이스홀더는 ${KEY} 형식이다 (예: ${PASSWORD}).</summary>
public sealed class MailTemplateService : IMailTemplateService
{
    private const string DefaultCulture = "ko-KR";
    private readonly string _basePath;

    public MailTemplateService(string basePath) => _basePath = basePath;

    public string? Render(string templateName, string culture, IReadOnlyDictionary<string, string> values)
    {
        var path = TemplatePath(templateName, culture);
        if (!File.Exists(path))
        {
            path = TemplatePath(templateName, DefaultCulture);
            if (!File.Exists(path)) return null;
        }

        var body = File.ReadAllText(path);
        foreach (var (key, value) in values)
            body = body.Replace("${" + key + "}", value);
        return body;
    }

    private string TemplatePath(string templateName, string culture)
        => Path.Combine(_basePath, $"{templateName}({culture}).xml");
}
