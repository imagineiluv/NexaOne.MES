using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NexaOne.Common;
using NexaOne.SHP.Application.Shp;
using NexaOne.SHP.Domain;
using NexaOne.CMMS.Application.Cmms;
using NexaOne.CMMS.Domain;
using NexaOne.EST.Application.Est;
using NexaOne.POM.Application.Pom;
using NexaOne.POM.Domain;
using NexaOne.RMS.Application.Rms;
using NexaOne.RMS.Domain;

namespace NexaOne.API.Controllers;

[ApiController]
[Route("api/v1/dashboard")]
[Authorize]
[ProducesErrorResponseType(typeof(Error))]
public class DashboardController(
    EquipmentAlarmService alarmService,
    CmmsService emsService,
    PomService ppmService,
    ShpService dlvService,
    RecipeService recipeService) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<DashboardSummaryResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetSummary(CancellationToken ct)
    {
        var tAlarms       = alarmService.GetActiveAlarmCountAsync(ct);
        var tIssued       = emsService.GetCountByStatusAsync(WorkOrderStatus.Issued, ct);
        var tInProgress   = emsService.GetCountByStatusAsync(WorkOrderStatus.InProgress, ct);
        var tPlans        = ppmService.GetCountByStatusAsync(ProductionPlanStatus.Released, ct);
        var tWaitApproval = recipeService.GetCountByStateAsync(RecipeApprovalState.WaitApproval, ct);
        var tApproved1    = recipeService.GetCountByStateAsync(RecipeApprovalState.Approved1, ct);
        var tDraftOrders  = dlvService.GetCountByStatusAsync(DeliveryOrderStatus.Draft, ct);
        var tConfirmed    = dlvService.GetCountByStatusAsync(DeliveryOrderStatus.Confirmed, ct);

        await Task.WhenAll(tAlarms, tIssued, tInProgress, tPlans,
                           tWaitApproval, tApproved1, tDraftOrders, tConfirmed);

        return Ok(new DashboardSummaryResponse(
            ActiveAlarms:          tAlarms.Result,
            IssuedWorkOrders:      tIssued.Result,
            InProgressWorkOrders:  tInProgress.Result,
            ReleasedPlans:         tPlans.Result,
            PendingRecipeApprovals: tWaitApproval.Result + tApproved1.Result,
            OpenDeliveryOrders:    tDraftOrders.Result + tConfirmed.Result
        ));
    }
}

public record DashboardSummaryResponse(
    int ActiveAlarms,
    int IssuedWorkOrders,
    int InProgressWorkOrders,
    int ReleasedPlans,
    int PendingRecipeApprovals,
    int OpenDeliveryOrders);
