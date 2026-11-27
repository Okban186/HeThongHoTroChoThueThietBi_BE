using System.Security.Claims;
using HeThongHoTroChoThueThietBi.Common.FileStorage.Dtos;
using HeThongHoTroChoThueThietBi.Common.FileStorage.Models;
using HeThongHoTroChoThueThietBi.Features.Operations.DTOs;
using HeThongHoTroChoThueThietBi.Features.Operations.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HeThongHoTroChoThueThietBi.Features.Operations.Controllers;

/// <summary>
/// Cổng vận hành cho Kỹ thuật viên (role TECHNICIAN):
/// xem tổng quan kèm chuyến đang hoạt động, bắt đầu xuất phát (KTV trưởng),
/// quét/tra serial kiểm tra máy thuộc phiếu bàn giao, upload ảnh/video (<= 500MB),
/// hoàn tất bàn giao, báo giao thất bại và xem chi tiết đầy đủ sau khi giao xong.
/// </summary>
[ApiController]
[Route("api/technician")]
[Authorize(Roles = "TECHNICIAN")]
public sealed class TechnicianOperationsController(TechnicianOperationsService service) : ControllerBase
{
    [HttpGet("dashboard")]
    public async Task<ActionResult<TechnicianDashboardResponse>> GetDashboard(CancellationToken ct)
        => Ok(await service.GetDashboardAsync(CurrentUserId(), ct));

    [HttpGet("assignments")]
    public async Task<ActionResult<List<TechnicianAssignmentSummaryResponse>>> GetAssignments(
        [FromQuery] string? status,
        [FromQuery] string? type,
        CancellationToken ct)
        => Ok(await service.GetMyAssignmentsAsync(CurrentUserId(), status, type, ct));

    [HttpGet("assignments/{id:guid}")]
    public async Task<ActionResult<TechnicianAssignmentDetailResponse>> GetAssignmentDetail(Guid id, CancellationToken ct)
        => Ok(await service.GetAssignmentDetailAsync(id, CurrentUserId(), ct));

    /// <summary>Chỉ KTV trưởng: bấm Bắt đầu xuất phát chuyến đi (PENDING → IN_PROGRESS).</summary>
    [HttpPost("assignments/{id:guid}/start")]
    public async Task<ActionResult<TechnicianAssignmentDetailResponse>> StartAssignment(Guid id, CancellationToken ct)
        => Ok(await service.StartAssignmentAsync(id, CurrentUserId(), ct));

    /// <summary>Cấp vé upload ảnh (<=10MB) hoặc video (<=500MB). Query: folder=handover|incident|maintenance, mediaType=image|video.</summary>
    [HttpPost("upload-ticket")]
    public async Task<ActionResult<FileUploadTicket>> CreateEvidenceUploadTicket(
        [FromBody] FileUploadRequest request,
        [FromQuery] string? folder,
        [FromQuery] string? mediaType,
        CancellationToken ct)
        => Ok(await service.CreateEvidenceUploadTicketAsync(request, folder, mediaType, CurrentUserId(), ct));

    [HttpPost("confirm-upload")]
    public async Task<ActionResult<ConfirmFileUploadResponse>> ConfirmEvidenceUpload(
        [FromBody] ConfirmFileUploadRequest request,
        CancellationToken ct)
        => Ok(await service.ConfirmEvidenceUploadAsync(request, CurrentUserId(), ct));

    /// <summary>Quét barcode hoặc nhập mã serial kiểm tra xem thiết bị vật lý có thuộc phiếu bàn giao này hay không.</summary>
    [HttpGet("handovers/{handoverNoteId:guid}/lookup-serial")]
    public async Task<ActionResult<TechnicianHandoverLookupSerialResponse>> LookupHandoverSerial(
        Guid handoverNoteId,
        [FromQuery] string serial,
        CancellationToken ct)
        => Ok(await service.LookupHandoverSerialAsync(handoverNoteId, serial, CurrentUserId(), ct));

    /// <summary>Xem chi tiết đầy đủ một phiếu bàn giao (kể cả sau khi giao xong: ảnh/video, máy đạt, chi tiết từng máy lỗi, phiếu thu hồi, thanh toán).</summary>
    [HttpGet("handovers/{handoverNoteId:guid}")]
    public async Task<ActionResult<TechnicianHandoverStopDetailDto>> GetHandoverDetail(
        Guid handoverNoteId,
        CancellationToken ct)
        => Ok(await service.GetHandoverStopDetailAsync(handoverNoteId, CurrentUserId(), ct));

    /// <summary>Chỉ KTV trưởng: Hoàn tất bàn giao tại hiện trường (ảnh/video hiện trường; nếu có máy lỗi bắt buộc ảnh/video chung hoặc ảnh riêng từng máy).</summary>
    [HttpPost("handovers/{handoverNoteId:guid}/complete")]
    public async Task<ActionResult<TechnicianCompleteHandoverResponse>> CompleteHandover(
        Guid handoverNoteId,
        [FromBody] TechnicianCompleteHandoverRequest request,
        CancellationToken ct)
        => Ok(await service.CompleteHandoverAsync(handoverNoteId, request, CurrentUserId(), ct));

    /// <summary>Chỉ KTV trưởng: Báo giao không thành công (bắt buộc lý do + ảnh/video minh chứng; trả phiếu về Manager xếp lịch lại).</summary>
    [HttpPost("handovers/{handoverNoteId:guid}/fail")]
    public async Task<ActionResult<TechnicianFailHandoverResponse>> FailHandover(
        Guid handoverNoteId,
        [FromBody] TechnicianFailHandoverRequest request,
        CancellationToken ct)
        => Ok(await service.FailHandoverAsync(handoverNoteId, request, CurrentUserId(), ct));

    /// <summary>Chỉ KTV trưởng: Hoàn tất thu hồi máy tại điểm dừng (chuyển phiếu thu hồi DRAFT → PROCESSING mang về kho kiểm nhận).</summary>
    [HttpPost("retrievals/{returnId:guid}/complete")]
    public async Task<ActionResult<TechnicianCompleteRetrievalResponse>> CompleteRetrievalStop(
        Guid returnId,
        [FromBody] TechnicianCompleteRetrievalRequest request,
        CancellationToken ct)
        => Ok(await service.CompleteRetrievalStopAsync(returnId, request, CurrentUserId(), ct));

    private Guid CurrentUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
