using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexaOne.DLV.Application.Dlv;
using NexaOne.DLV.Domain;
using NexaOne.EMS.Application.Ems;
using NexaOne.EMS.Domain;
using NexaOne.EPT.Application.Ept;
using NexaOne.PPM.Application.Ppm;
using NexaOne.PPM.Domain;
using NexaOne.RMS.Application.Rms;
using NexaOne.RMS.Domain;

namespace NexaOne.API.Controllers;

[ApiController]
[Route("api/v1/dashboard")]
[Authorize]
public class DashboardController(
    EquipmentAlarmService alarmService,
    EmsService emsService,
    PpmService ppmService,
    DlvService dlvService,
    RecipeService recipeService) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetSummary(CancellationToken ct)
    {
        var tAlarms       = alarmService.GetActiveAlarmCountAsync(ct);
        var tIssued       = emsService.GetByStatusAsync(WorkOrderStatus.Issued, ct);
        var tInProgress   = emsService.GetByStatusAsync(WorkOrderStatus.InProgress, ct);
        var tPlans        = ppmService.GetCountByStatusAsync(ProductionPlanStatus.Released, ct);
        var tWaitApproval = recipeService.GetByStateAsync(RecipeApprovalState.WaitApproval, ct);
        var tApproved1    = recipeService.GetByStateAsync(RecipeApprovalState.Approved1, ct);
        var tDraftOrders  = dlvService.GetCountByStatusAsync(DeliveryOrderStatus.Draft, ct);
        var tConfirmed    = dlvService.GetCountByStatusAsync(DeliveryOrderStatus.Confirmed, ct);

        await Task.WhenAll(tAlarms, tIssued, tInProgress, tPlans,
                           tWaitApproval, tApproved1, tDraftOrders, tConfirmed);

        var pendingApprovals = (tWaitApproval.Result.IsSuccess ? tWaitApproval.Result.Value.Count : 0)
                             + (tApproved1.Result.IsSuccess ? tApproved1.Result.Value.Count : 0);

        return Ok(new DashboardSummaryResponse(
            ActiveAlarms:          tAlarms.Result,
            IssuedWorkOrders:      tIssued.Result.IsSuccess ? tIssued.Result.Value.Count : 0,
            InProgressWorkOrders:  tInProgress.Result.IsSuccess ? tInProgress.Result.Value.Count : 0,
            ReleasedPlans:         tPlans.Result,
            PendingRecipeApprovals: pendingApprovals,
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
