using System.Reflection;

namespace NexaOne.ServiceContracts;

/// <summary>
/// Spring 모듈 Bean을 호스트 DI에 연결할 계약 인터페이스임을 나타내는 표식이다.
/// 구현 클래스가 아니라 Default ALC에서 공유되는 계약 인터페이스에만 적용한다.
/// </summary>
public interface INexaModuleBridge
{
}

/// <summary>
/// 모듈 브리지 계약을 Spring 서비스 컨텍스트와 Bean 이름에 연결한다.
/// </summary>
[AttributeUsage(AttributeTargets.Interface, AllowMultiple = false, Inherited = false)]
public sealed class NexaModuleBridgeAttribute : Attribute
{
    /// <summary>브리지 연결 메타데이터를 생성한다.</summary>
    /// <param name="module">Spring 서비스 컨텍스트 이름.</param>
    /// <param name="beanName">서비스 컨텍스트에 등록된 Bean 이름.</param>
    public NexaModuleBridgeAttribute(string module, string beanName)
    {
        Module = module;
        BeanName = beanName;
    }

    /// <summary>Spring 서비스 컨텍스트 이름을 가져온다.</summary>
    public string Module { get; }

    /// <summary>서비스 컨텍스트에 등록된 Bean 이름을 가져온다.</summary>
    public string BeanName { get; }
}

/// <summary>
/// 공유 계약 형식과 해당 계약을 구현하는 Spring 모듈 Bean의 연결 정보다.
/// </summary>
/// <param name="ContractType">Default ALC에서 공유되는 브리지 계약 인터페이스.</param>
/// <param name="Module">Spring 서비스 컨텍스트 이름.</param>
/// <param name="BeanName">서비스 컨텍스트에 등록된 Bean 이름.</param>
public sealed record NexaModuleBridgeDescriptor(Type ContractType, string Module, string BeanName);

/// <summary>발견된 모듈 브리지 계약을 조회하는 읽기 전용 카탈로그다.</summary>
public interface INexaModuleBridgeCatalog
{
    /// <summary>모듈, Bean, 계약 형식 순으로 정렬된 브리지 연결 목록을 가져온다.</summary>
    IReadOnlyList<NexaModuleBridgeDescriptor> Descriptors { get; }

    /// <summary>계약 형식에 대응하는 브리지 연결 정보를 조회한다.</summary>
    /// <param name="contractType">조회할 브리지 계약 인터페이스.</param>
    /// <param name="descriptor">발견된 연결 정보.</param>
    /// <returns>계약이 등록되어 있으면 <see langword="true"/>, 아니면 <see langword="false"/>.</returns>
    bool TryGet(Type contractType, out NexaModuleBridgeDescriptor descriptor);
}

/// <summary>
/// 명시적으로 전달된 어셈블리에서 모듈 브리지 계약을 발견하고 검증한다.
/// 현재 AppDomain 전체를 암묵적으로 검색하지 않아 플러그인 ALC의 구현 형식을 호스트가 잘못 활성화하는 일을 막는다.
/// </summary>
public sealed class NexaModuleBridgeCatalog : INexaModuleBridgeCatalog
{
    private readonly IReadOnlyDictionary<Type, NexaModuleBridgeDescriptor> _descriptorsByContract;

    private NexaModuleBridgeCatalog(IReadOnlyList<NexaModuleBridgeDescriptor> descriptors)
    {
        Descriptors = descriptors;
        _descriptorsByContract = descriptors.ToDictionary(static descriptor => descriptor.ContractType);
    }

    /// <inheritdoc />
    public IReadOnlyList<NexaModuleBridgeDescriptor> Descriptors { get; }

    /// <inheritdoc />
    public bool TryGet(Type contractType, out NexaModuleBridgeDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(contractType);
        return _descriptorsByContract.TryGetValue(contractType, out descriptor!);
    }

    /// <summary>
    /// 지정한 어셈블리만 검색해 모듈 브리지 카탈로그를 만든다.
    /// 표식 오용, 메타데이터 누락·중복 또는 같은 모듈/Bean 연결 중복은 시작 단계에서 즉시 실패한다.
    /// </summary>
    /// <param name="assemblies">브리지 계약을 포함하는 명시적 어셈블리 목록.</param>
    /// <returns>검증과 결정적 정렬이 완료된 브리지 카탈로그.</returns>
    /// <exception cref="ArgumentException">검색할 어셈블리가 없거나 목록에 <see langword="null"/>이 포함된 경우.</exception>
    /// <exception cref="InvalidOperationException">브리지 계약 또는 연결 메타데이터가 유효하지 않은 경우.</exception>
    public static NexaModuleBridgeCatalog Discover(params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        if (assemblies.Length == 0)
            throw new ArgumentException("브리지 계약을 검색할 어셈블리를 하나 이상 지정해야 합니다.", nameof(assemblies));
        if (assemblies.Any(static assembly => assembly is null))
            throw new ArgumentException("브리지 계약 검색 어셈블리 목록에는 null을 포함할 수 없습니다.", nameof(assemblies));

        var descriptors = new List<NexaModuleBridgeDescriptor>();
        var contracts = new HashSet<Type>();
        var bindings = new Dictionary<(string Module, string BeanName), Type>();

        foreach (var assembly in assemblies.OrderBy(static assembly => assembly.FullName, StringComparer.Ordinal))
        {
            foreach (var type in GetLoadableTypes(assembly)
                         .OrderBy(static type => type.FullName ?? type.Name, StringComparer.Ordinal))
            {
                if (type == typeof(INexaModuleBridge))
                    continue;

                var attributes = type
                    .GetCustomAttributes<NexaModuleBridgeAttribute>(inherit: false)
                    .ToArray();
                var hasMarker = typeof(INexaModuleBridge).IsAssignableFrom(type);

                if (!hasMarker && attributes.Length == 0)
                    continue;
                if (!type.IsInterface)
                    throw new InvalidOperationException(
                        $"모듈 브리지 표식은 계약 인터페이스에만 사용할 수 있습니다: '{type.FullName}'.");
                if (!hasMarker)
                    throw new InvalidOperationException(
                        $"'{type.FullName}'에 {nameof(NexaModuleBridgeAttribute)}가 있지만 {nameof(INexaModuleBridge)}를 상속하지 않습니다.");
                if (attributes.Length == 0)
                    throw new InvalidOperationException(
                        $"모듈 브리지 계약 '{type.FullName}'에 {nameof(NexaModuleBridgeAttribute)}가 없습니다.");
                if (attributes.Length > 1)
                    throw new InvalidOperationException(
                        $"모듈 브리지 계약 '{type.FullName}'에 {nameof(NexaModuleBridgeAttribute)}가 중복 선언되었습니다.");

                var attribute = attributes[0];
                if (string.IsNullOrWhiteSpace(attribute.Module))
                    throw new InvalidOperationException($"모듈 브리지 계약 '{type.FullName}'의 Module 값이 비어 있습니다.");
                if (string.IsNullOrWhiteSpace(attribute.BeanName))
                    throw new InvalidOperationException($"모듈 브리지 계약 '{type.FullName}'의 BeanName 값이 비어 있습니다.");

                var module = attribute.Module.Trim();
                var beanName = attribute.BeanName.Trim();
                if (!contracts.Add(type))
                    throw new InvalidOperationException($"모듈 브리지 계약 '{type.FullName}'이 중복 발견되었습니다.");

                var binding = (Module: module, BeanName: beanName);
                if (bindings.TryGetValue(binding, out var existingContract))
                    throw new InvalidOperationException(
                        $"모듈/Bean 연결 '{module}/{beanName}'이 계약 '{existingContract.FullName}'과 '{type.FullName}'에 중복 선언되었습니다.");

                bindings.Add(binding, type);
                descriptors.Add(new NexaModuleBridgeDescriptor(type, module, beanName));
            }
        }

        var ordered = descriptors
            .OrderBy(static descriptor => descriptor.Module, StringComparer.Ordinal)
            .ThenBy(static descriptor => descriptor.BeanName, StringComparer.Ordinal)
            .ThenBy(static descriptor => descriptor.ContractType.FullName ?? descriptor.ContractType.Name, StringComparer.Ordinal)
            .ToList()
            .AsReadOnly();

        return new NexaModuleBridgeCatalog(ordered);
    }

    /// <summary>
    /// 일부 종속 형식을 읽지 못한 어셈블리에서도 로드 가능한 형식만 안전하게 반환한다.
    /// 브리지 계약 어셈블리는 가능한 한 Default ALC 공유 계약만 포함해야 한다.
    /// </summary>
    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.OfType<Type>();
        }
    }
}
