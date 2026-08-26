using System.Reflection;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NexaOne.Common;
using NexaOne.Common.Security;
using NexaOne.Server.Gateway;
using NexaOne.ServiceContracts.Ems;
using Xunit;

namespace NexaOne.ServerTests;

public sealed class SparePartControllerTests
{
    [Fact]
    public async Task Mutation_fails_closed_without_an_authenticated_actor()
    {
        var bridge = new FakeBridge();
        var controller = Controller(bridge, new ClaimsPrincipal(new ClaimsIdentity("test")));

        var result = await controller.SaveStockPolicy(
            "ROUTE-PART",
            Policy("BODY-PART", "spoofed"),
            CancellationToken.None);

        result.Should().BeOfType<UnauthorizedResult>();
        bridge.InvocationCount.Should().Be(0);
    }

    [Fact]
    public async Task Routes_and_claim_actor_override_untrusted_write_body_values()
    {
        var bridge = new FakeBridge();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "login-maintainer")], "test"));
        var controller = Controller(bridge, principal);

        var policyResult = await controller.SaveStockPolicy(
            "ROUTE-PART", Policy("BODY-PART", "spoofed"), CancellationToken.None);
        var supplierResult = await controller.SaveSupplier(
            "ROUTE-SUPPLIER",
            new SparePartSupplierCommand(
                "BODY-SUPPLIER", "PART-1", "VENDOR-1", 1, 1m, null, null,
                false, true, 0, "supplier-key", ActorId: "spoofed"),
            CancellationToken.None);
        var bomResult = await controller.SaveEquipmentBom(
            "ROUTE-BOM",
            new EquipmentPartBomCommand(
                "BODY-BOM", "PART-1", 1m, "EQ-1", null, null, null, null,
                null, true, 0, "bom-key", "spoofed"),
            CancellationToken.None);

        policyResult.Should().BeOfType<OkObjectResult>();
        supplierResult.Should().BeOfType<OkObjectResult>();
        bomResult.Should().BeOfType<OkObjectResult>();
        bridge.LastPolicy!.PartId.Should().Be("ROUTE-PART");
        bridge.LastPolicy.ActorId.Should().Be("login-maintainer");
        bridge.LastSupplier!.PartSupplierId.Should().Be("ROUTE-SUPPLIER");
        bridge.LastSupplier.ActorId.Should().Be("login-maintainer");
        bridge.LastBom!.BomItemId.Should().Be("ROUTE-BOM");
        bridge.LastBom.ActorId.Should().Be("login-maintainer");
    }

    [Theory]
    [InlineData(nameof(SparePartController.SaveStockPolicy), Permissions.EmsManage)]
    [InlineData(nameof(SparePartController.SaveSupplier), Permissions.EmsManage)]
    [InlineData(nameof(SparePartController.SaveEquipmentBom), Permissions.EmsManage)]
    [InlineData(nameof(SparePartController.RecommendReplenishment), Permissions.EmsRead)]
    public void Endpoints_require_the_narrow_ems_permission(string actionName, string permission)
    {
        var method = typeof(SparePartController).GetMethod(
            actionName, BindingFlags.Instance | BindingFlags.Public);

        method.Should().NotBeNull();
        method!.GetCustomAttribute<RequirePermissionAttribute>()!.Policy.Should().Be(
            RequirePermissionAttribute.PolicyPrefix + permission);
    }

    private static SparePartController Controller(ISparePartBridge bridge, ClaimsPrincipal principal)
        => new(bridge)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = principal },
            },
        };

    private static SparePartStockPolicyCommand Policy(string partId, string actor) => new(
        partId, 1m, 2m, 5m, 0m, 1m, 0.95m, 7, true, 0, "policy-key", actor);

    private sealed class FakeBridge : ISparePartBridge
    {
        public int InvocationCount { get; private set; }
        public SparePartStockPolicyCommand? LastPolicy { get; private set; }
        public SparePartSupplierCommand? LastSupplier { get; private set; }
        public EquipmentPartBomCommand? LastBom { get; private set; }

        public Task<Result<SparePartStockPolicyDto>> SaveStockPolicyAsync(
            SparePartStockPolicyCommand command,
            CancellationToken ct = default)
        {
            InvocationCount++;
            LastPolicy = command;
            return Task.FromResult(Result.Success(new SparePartStockPolicyDto(
                command.PartId, command.SafetyStock, command.ReorderPoint, command.TargetStock,
                command.ReservedQuantity, command.AverageDailyUsage, command.ServiceLevel,
                command.ReviewCycleDays, command.IsActive, 1, command.ActorId!, DateTime.UtcNow)));
        }

        public Task<Result<SparePartSupplierDto>> SaveSupplierAsync(
            SparePartSupplierCommand command,
            CancellationToken ct = default)
        {
            InvocationCount++;
            LastSupplier = command;
            return Task.FromResult(Result.Success(new SparePartSupplierDto(
                command.PartSupplierId, command.PartId, command.VendorId,
                command.VendorPartNumber, command.LeadTimeDays, command.MinimumOrderQuantity,
                command.UnitPrice, command.Currency, command.IsPrimary, command.IsActive,
                1, command.ActorId!, DateTime.UtcNow)));
        }

        public Task<Result<EquipmentPartBomDto>> SaveEquipmentBomAsync(
            EquipmentPartBomCommand command,
            CancellationToken ct = default)
        {
            InvocationCount++;
            LastBom = command;
            return Task.FromResult(Result.Success(new EquipmentPartBomDto(
                command.BomItemId, command.PartId, command.EquipmentId,
                command.EquipmentClassId, command.QuantityPer, command.Criticality,
                command.ReplacementCycleDays, command.ReplacementCycleCount,
                command.PositionCode, command.IsActive, 1, command.ActorId!, DateTime.UtcNow)));
        }

        public Task<Result<SparePartReplenishmentDto>> RecommendReplenishmentAsync(
            string partId,
            CancellationToken ct = default)
            => Task.FromResult(Result.Success(new SparePartReplenishmentDto(
                partId, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m,
                false, null, null, null, null, "test")));
    }
}
