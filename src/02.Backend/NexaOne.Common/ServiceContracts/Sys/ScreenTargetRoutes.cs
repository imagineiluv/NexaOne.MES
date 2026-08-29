namespace NexaOne.ServiceContracts.Sys;

/// <summary>
/// 화면 정의의 채널과 진입 경로를 하나의 규칙으로 정규화합니다.
/// Designer, 코드 시드 가져오기, SYS 저장소가 같은 경로 계약을 공유하도록 순수 함수로 둡니다.
/// </summary>
public static class ScreenTargetRoutes
{
    public const string Mes = "MES";
    public const string Mobile = "MOBILE";
    public const string Pop = "POP";

    /// <summary>
    /// 채널이 비어 있으면 MES를 사용하고, 경로가 비어 있으면 채널별 기본 경로를 계산합니다.
    /// 호출자가 경로를 지정했다면 계산된 기본 경로와 정확히 일치해야 합니다.
    /// </summary>
    public static ScreenTarget Resolve(
        string uiId,
        string? targetChannel = null,
        string? entryPath = null)
    {
        if (string.IsNullOrWhiteSpace(uiId))
            throw new ArgumentException("UiId is required.", nameof(uiId));

        var canonicalUiId = uiId.Trim();
        var channel = string.IsNullOrWhiteSpace(targetChannel)
            ? Mes
            : targetChannel.Trim().ToUpperInvariant();
        var expectedPath = channel switch
        {
            Mes => $"/meta/{canonicalUiId}",
            Mobile => $"/Mobile/{canonicalUiId}",
            Pop => $"/POP/{canonicalUiId}",
            _ => throw new ArgumentOutOfRangeException(
                nameof(targetChannel), targetChannel, "TargetChannel must be MES, MOBILE, or POP."),
        };

        if (!string.IsNullOrWhiteSpace(entryPath)
            && !string.Equals(entryPath.Trim(), expectedPath, StringComparison.Ordinal))
            throw new ArgumentException(
                $"EntryPath must be '{expectedPath}' for {channel}.", nameof(entryPath));

        return new ScreenTarget(channel, expectedPath);
    }
}

/// <summary>정규화된 화면 진입 채널과 완전 경로입니다.</summary>
public sealed record ScreenTarget(string TargetChannel, string EntryPath);
