using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using NexaOne.Common;
using NexaOne.ServiceContracts.Mdm;
using Xunit;

namespace NexaOne.ServerTests;

/// <summary>MDM 얇은 브리지 컨트롤러 HTTP 매핑 검증(ADR-008) — modules OFF + 가짜 IMdmEquipmentBridge/
/// IMdmMasterBridge 주입으로 쓰기 권한(403/200)·생성 DTO·상태전이(204)·도메인 검증 실패(400)·
/// CreateCode CodeClass 미존재(404)를 Spring/ALC 없이 결정적으로 검증한다.</summary>
public sealed class MdmBridgeControllerTests : IClassFixture<MdmBridgeControllerTests.BridgeFactory>
{
    private const string Secret = "phase-mdm-bridge-e2e-jwt-secret-key-at-least-32b!!";
    private const string Issuer = "nexaone-mdm-bridge-test";
    private readonly BridgeFactory _factory;
    public MdmBridgeControllerTests(BridgeFactory factory) => _factory = factory;

    public sealed class BridgeFactory : WebApplicationFactory<Program>
    {
        public readonly string DbPath = Path.Combine(Path.GetTempPath(), $"nexaone-mdm-bridge-{Guid.NewGuid():N}.db");
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Server:Modules:Enabled", "false");
            builder.UseSetting("Database:Provider", "Sqlite");
            builder.UseSetting("ConnectionStrings:NexaOne", $"Data Source={DbPath};Foreign Keys=False");
            builder.UseSetting("Jwt:SecretKey", Secret);
            builder.UseSetting("Jwt:Issuer", Issuer);
            builder.UseSetting("Jwt:Audience", Issuer);
            builder.UseSetting("RateLimiting:Enabled", "false");
            builder.ConfigureTestServices(s =>
            {
                s.AddSingleton<IMdmEquipmentBridge>(new FakeEquipmentBridge());
                s.AddSingleton<IMdmMasterBridge>(new FakeMasterBridge());
            });
        }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try { if (File.Exists(DbPath)) File.Delete(DbPath); } catch { /* 무시 */ }
        }
    }

    private sealed class FakeEquipmentBridge : IMdmEquipmentBridge
    {
        public Task<Result<EquipmentDto>> CreateEquipmentAsync(
            string equipmentId, string equipmentName, string plantId, string areaId, string equipmentType,
            string? parentEquipmentId = null, string vendor = "", string model = "", string equipmentClassId = "",
            CancellationToken ct = default)
            => Task.FromResult(string.IsNullOrWhiteSpace(equipmentId)
                ? Result.Failure<EquipmentDto>(Error.Validation(nameof(equipmentId), "Equipment ID is required."))
                : Result.Success(new EquipmentDto(
                    equipmentId, equipmentName, "", plantId, areaId, equipmentType,
                    parentEquipmentId, vendor, model, equipmentClassId, "Valid")));

        public Task<Result> DeactivateEquipmentAsync(string equipmentId, CancellationToken ct = default)
            => Task.FromResult(equipmentId == "MISSING"
                ? Result.Failure(Error.NotFound("Equipment", equipmentId))
                : Result.Success());

        public Task<Result<EquipmentDto>> UpdateEquipmentAsync(
            string equipmentId, string name, string description, string equipmentType, string vendor, string model,
            CancellationToken ct = default)
            => Task.FromResult(equipmentId == "MISSING"
                ? Result.Failure<EquipmentDto>(Error.NotFound("Equipment", equipmentId))
                : Result.Success(new EquipmentDto(
                    equipmentId, name, description, "P1", "A1", equipmentType, null, vendor, model, "", "Valid")));
    }

    private sealed class FakeMasterBridge : IMdmMasterBridge
    {
        public Task<Result<PlantDto>> CreatePlantAsync(
            string plantId, string plantName, string country, string timeZone, CancellationToken ct = default)
            => Task.FromResult(string.IsNullOrWhiteSpace(plantId)
                ? Result.Failure<PlantDto>(Error.Validation(nameof(plantId), "Plant ID is required."))
                : Result.Success(new PlantDto(plantId, plantName, "", country, timeZone)));

        public Task<Result<AreaDto>> CreateAreaAsync(
            string areaId, string areaName, string plantId, CancellationToken ct = default)
            => Task.FromResult(Result.Success(new AreaDto(areaId, areaName, "", plantId)));

        public Task<Result<ProductDto>> CreateProductAsync(
            string productId, string productName, string productType, string unit, CancellationToken ct = default)
            => Task.FromResult(Result.Success(new ProductDto(productId, productName, "", productType, unit, "Valid")));

        public Task<Result<CodeClassDto>> CreateCodeClassAsync(
            string codeClassId, string codeClassName, CancellationToken ct = default)
            => Task.FromResult(Result.Success(new CodeClassDto(codeClassId, codeClassName, "")));

        public Task<Result<CodeDto>> CreateCodeAsync(
            string codeId, string codeClassId, string codeName, int sortOrder = 0, CancellationToken ct = default)
            => Task.FromResult(codeClassId == "NO_CLASS"
                ? Result.Failure<CodeDto>(Error.NotFound("CodeClass", codeClassId))
                : Result.Success(new CodeDto(codeId, codeClassId, codeName, sortOrder, "Valid")));
    }

    private HttpClient Client(params string[] permissions)
    {
        var client = _factory.CreateClient();
        var creds = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Secret)), SecurityAlgorithms.HmacSha256);
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "mdm-bridge-tester") };
        claims.AddRange(permissions.Select(p => new Claim(NexaOne.Common.Security.Permissions.ClaimType, p)));
        var token = new JwtSecurityToken(Issuer, Issuer, claims, expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: creds);
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return client;
    }

    // ── Equipment ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateEquipment_without_mdm_manage_is_forbidden()
    {
        var res = await Client("fdc:read").PostAsJsonAsync("/api/v1/mdm/equipment",
            new { equipmentId = "EQ1", equipmentName = "Press", plantId = "P1", areaId = "A1", equipmentType = "Press" });
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden, "mdm:manage 미보유 쓰기는 403");
    }

    [Fact]
    public async Task CreateEquipment_with_mdm_manage_returns_200_with_dto()
    {
        var res = await Client("mdm:manage").PostAsJsonAsync("/api/v1/mdm/equipment",
            new { equipmentId = "EQ1", equipmentName = "Press", plantId = "P1", areaId = "A1", equipmentType = "Press" });
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await res.Content.ReadFromJsonAsync<EquipmentDto>();
        dto!.EquipmentId.Should().Be("EQ1");
        dto.ValidState.Should().Be("Valid");
    }

    [Fact]
    public async Task CreateEquipment_with_empty_id_maps_to_400()
    {
        var res = await Client("mdm:manage").PostAsJsonAsync("/api/v1/mdm/equipment",
            new { equipmentId = "", equipmentName = "Press", plantId = "P1", areaId = "A1", equipmentType = "Press" });
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, "도메인 검증 실패(빈 ID)는 400");
    }

    [Fact]
    public async Task DeactivateEquipment_success_returns_204()
    {
        var res = await Client("mdm:manage").PostAsync("/api/v1/mdm/equipment/EQ1/deactivate", content: null);
        res.StatusCode.Should().Be(HttpStatusCode.NoContent, "성공 상태전이는 204");
    }

    [Fact]
    public async Task DeactivateEquipment_missing_maps_to_404()
    {
        var res = await Client("mdm:manage").PostAsync("/api/v1/mdm/equipment/MISSING/deactivate", content: null);
        res.StatusCode.Should().Be(HttpStatusCode.NotFound, "미존재 설비 비활성은 404");
    }

    [Fact]
    public async Task UpdateEquipment_success_returns_200_with_dto()
    {
        var res = await Client("mdm:manage").PutAsJsonAsync("/api/v1/mdm/equipment/EQ1",
            new { name = "Press-2", description = "rev", equipmentType = "Press", vendor = "V", model = "M" });
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await res.Content.ReadFromJsonAsync<EquipmentDto>();
        dto!.EquipmentName.Should().Be("Press-2");
        dto.Description.Should().Be("rev");
    }

    [Fact]
    public async Task UpdateEquipment_without_mdm_manage_is_forbidden()
    {
        var res = await Client().PutAsJsonAsync("/api/v1/mdm/equipment/EQ1",
            new { name = "Press-2", description = "rev", equipmentType = "Press", vendor = "V", model = "M" });
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── Master ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreatePlant_with_mdm_manage_returns_200_with_dto()
    {
        var res = await Client("mdm:manage").PostAsJsonAsync("/api/v1/mdm/plants",
            new { plantId = "P1", plantName = "Seoul", country = "KR", timeZone = "Asia/Seoul" });
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await res.Content.ReadFromJsonAsync<PlantDto>();
        dto!.PlantId.Should().Be("P1");
        dto.Country.Should().Be("KR");
    }

    [Fact]
    public async Task CreatePlant_without_mdm_manage_is_forbidden()
    {
        var res = await Client("fdc:read").PostAsJsonAsync("/api/v1/mdm/plants",
            new { plantId = "P1", plantName = "Seoul", country = "KR", timeZone = "Asia/Seoul" });
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task CreatePlant_with_empty_id_maps_to_400()
    {
        var res = await Client("mdm:manage").PostAsJsonAsync("/api/v1/mdm/plants",
            new { plantId = "", plantName = "Seoul", country = "KR", timeZone = "Asia/Seoul" });
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, "도메인 검증 실패(빈 ID)는 400");
    }

    [Fact]
    public async Task CreateArea_returns_200_with_dto()
    {
        var res = await Client("mdm:manage").PostAsJsonAsync("/api/v1/mdm/areas",
            new { areaId = "A1", areaName = "Line-1", plantId = "P1" });
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await res.Content.ReadFromJsonAsync<AreaDto>())!.AreaId.Should().Be("A1");
    }

    [Fact]
    public async Task CreateProduct_returns_200_with_dto()
    {
        var res = await Client("mdm:manage").PostAsJsonAsync("/api/v1/mdm/products",
            new { productId = "PR1", productName = "Widget", productType = "FG", unit = "EA" });
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await res.Content.ReadFromJsonAsync<ProductDto>())!.ProductId.Should().Be("PR1");
    }

    [Fact]
    public async Task CreateCodeClass_returns_200_with_dto()
    {
        var res = await Client("mdm:manage").PostAsJsonAsync("/api/v1/mdm/code-classes",
            new { codeClassId = "CC1", codeClassName = "DefectType" });
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await res.Content.ReadFromJsonAsync<CodeClassDto>())!.CodeClassId.Should().Be("CC1");
    }

    [Fact]
    public async Task CreateCode_returns_200_with_dto()
    {
        var res = await Client("mdm:manage").PostAsJsonAsync("/api/v1/mdm/codes",
            new { codeId = "C1", codeClassId = "CC1", codeName = "Scratch", sortOrder = 1 });
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await res.Content.ReadFromJsonAsync<CodeDto>())!.CodeId.Should().Be("C1");
    }

    [Fact]
    public async Task CreateCode_with_missing_code_class_maps_to_404()
    {
        var res = await Client("mdm:manage").PostAsJsonAsync("/api/v1/mdm/codes",
            new { codeId = "C1", codeClassId = "NO_CLASS", codeName = "Scratch", sortOrder = 1 });
        res.StatusCode.Should().Be(HttpStatusCode.NotFound, "CodeClass 미존재 시 CreateCode는 404");
    }

    [Fact]
    public async Task CreateCode_without_mdm_manage_is_forbidden()
    {
        var res = await Client().PostAsJsonAsync("/api/v1/mdm/codes",
            new { codeId = "C1", codeClassId = "CC1", codeName = "Scratch", sortOrder = 1 });
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
