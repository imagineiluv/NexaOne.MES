using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;

namespace NexaOne.Common.Configuration;

/// <summary>
/// Spring XML의 <c>appConfiguration</c> 빈 → 호스트 <see cref="IConfiguration"/> 브리지.
/// <para>
/// 종전에는 XML이 빈 <c>ConfigurationManager</c> 새 인스턴스를 생성해 모듈 리포의 설정 읽기
/// (예: <c>Events:Outbox:Enabled</c>)가 어떤 환경에서도 켜질 수 없었다 — 호스트 env/appsettings와
/// 완전히 단절된 실버그. Program.cs가 CreateServer <b>이전에</b> <see cref="Root"/>를 호스트 구성으로
/// 설정하고, Spring은 이 타입을 생성해 모든 조회를 Root로 위임한다(모듈 코드는 IConfiguration 그대로).
/// Root 미설정(리포 단독 단위테스트 등)이면 빈 구성처럼 동작한다.
/// </para>
/// </summary>
public sealed class SpringHostConfiguration : IConfiguration
{
    /// <summary>호스트 구성 루트 — Program.cs가 Spring 컨텍스트 생성 전에 1회 설정한다.</summary>
    public static IConfiguration? Root { get; set; }

    public string? this[string key]
    {
        get => Root?[key];
        set { if (Root is not null) Root[key] = value; }
    }

    public IConfigurationSection GetSection(string key)
        => Root?.GetSection(key) ?? new EmptySection(key);

    public IEnumerable<IConfigurationSection> GetChildren()
        => Root?.GetChildren() ?? Enumerable.Empty<IConfigurationSection>();

    public IChangeToken GetReloadToken()
        => Root?.GetReloadToken() ?? InertChangeToken.Instance;

    // Root 부재 시의 빈 섹션(Abstractions만으로 구현 — Common은 ConfigurationBuilder 미참조).
    private sealed class EmptySection : IConfigurationSection
    {
        public EmptySection(string key) { Key = key; Path = key; }
        public string Key { get; }
        public string Path { get; }
        public string? Value { get => null; set { } }
        public string? this[string key] { get => null; set { } }
        public IConfigurationSection GetSection(string key) => new EmptySection(key);
        public IEnumerable<IConfigurationSection> GetChildren() => Enumerable.Empty<IConfigurationSection>();
        public IChangeToken GetReloadToken() => InertChangeToken.Instance;
    }

    private sealed class InertChangeToken : IChangeToken
    {
        public static readonly InertChangeToken Instance = new();
        public bool HasChanged => false;
        public bool ActiveChangeCallbacks => false;
        public IDisposable RegisterChangeCallback(Action<object?> callback, object? state) => NoopDisposable.Instance;
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new();
        public void Dispose() { }
    }
}
