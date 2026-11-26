using System.Security.Claims;
using HeThongHoTroChoThueThietBi.Common.FileStorage.Dtos;
using HeThongHoTroChoThueThietBi.Common.FileStorage.Models;
using HeThongHoTroChoThueThietBi.Features.Customer.DTOs;
using HeThongHoTroChoThueThietBi.Features.Customer.Services;
using HeThongHoTroChoThueThietBi.Features.Finance.DTOs;
using HeThongHoTroChoThueThietBi.Features.Finance.Services;
using HeThongHoTroChoThueThietBi.Features.Operations.DTOs;
using HeThongHoTroChoThueThietBi.Features.Operations.Services;
using HeThongHoTroChoThueThietBi.Features.Rental.DTOs;
using HeThongHoTroChoThueThietBi.Features.Rental.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HeThongHoTroChoThueThietBi.Features.Customer.Controllers;

/// <summary>
/// Cổng khách hàng (role COMPANY): thông tin công ty, tổng quan, hợp đồng của công ty mình
/// (chỉ hiện từ khi đã được duyệt) và báo sự cố máy đang thuê.
/// </summary>
[ApiController]
[Route("api/customer")]
[Authorize(Roles = "COMPANY")]
public sealed class CustomerController(
    CustomerPortalService portalService,
    ContractService contractService,
    CustomerIncidentService incidentService,
    HandoverService handoverService,
    PaymentService paymentService) : ControllerBase
{
    [HttpGet("company")]
    public async Task<ActionResult<CustomerCompanyResponse>> GetCompany(CancellationToken cancellationToken)
    {
        return Ok(await portalService.GetCompanyAsync(CurrentUserId(), cancellationToken));
    }

    [HttpGet("dashboard")]
    public async Task<ActionResult<CustomerDashboardResponse>> GetDashboard(CancellationToken cancellationToken)
    {
        return Ok(await portalService.GetDashboardAsync(CurrentUserId(), cancellationToken));
    }

    [HttpGet("contracts")]
    public async Task<ActionResult<List<RentalContractResponse>>> GetContracts(
        [FromQuery] string? status,
        CancellationToken cancellationToken)
    {
        var companyId = await portalService.GetCompanyIdAsync(CurrentUserId(), cancellationToken);
        return Ok(await contractService.GetCompanyContractsAsync(companyId, status, CurrentUserId(), cancellationToken));
    }

    [HttpGet("contracts/{id:guid}")]
    public async Task<ActionResult<RentalContractDetailResponse>> GetContract(Guid id, CancellationToken cancellationToken)
    {
        var companyId = await portalService.GetCompanyIdAsync(CurrentUserId(), cancellationToken);
        return Ok(await contractService.GetCompanyContractDetailAsync(id, companyId, CurrentUserId(), cancellationToken));
    }

    /// <summary>Hợp đồng PDF điền sẵn từ mẫu (bản để ký).</summary>
    [HttpGet("contracts/{id:guid}/pdf")]
    public async Task<IActionResult> ExportPdf(Guid id, CancellationToken cancellationToken)
    {
        var companyId = await portalService.GetCompanyIdAsync(CurrentUserId(), cancellationToken);
        var (fileName, content) = await contractService.ExportCompanyPdfAsync(id, companyId, CurrentUserId(), cancellationToken);
        return File(content, "application/pdf", fileName);
    }

    /// <summary>Theo dõi bàn giao: các đợt giao của hợp đồng (trừ phiếu đã hủy) và máy trong từng đợt.</summary>
    [HttpGet("contracts/{id:guid}/handovers")]
    public async Task<ActionResult<List<HandoverNoteDetailResponse>>> GetContractHandovers(
        Guid id,
        CancellationToken cancellationToken)
    {
        var companyId = await portalService.GetCompanyIdAsync(CurrentUserId(), cancellationToken);
        await contractService.EnsureCustomerCanViewAsync(id, companyId, cancellationToken);
        return Ok(await handoverService.GetContractDeliveriesAsync(id, cancellationToken));
    }

    /// <summary>Tổng quan chi phí, các đợt thanh toán tiền thuê, khoản phạt/bồi thường và giao dịch thanh toán.</summary>
    [HttpGet("contracts/{id:guid}/billing")]
    public async Task<ActionResult<ContractBillingSummaryResponse>> GetContractBilling(
        Guid id,
        CancellationToken cancellationToken)
    {
        var companyId = await portalService.GetCompanyIdAsync(CurrentUserId(), cancellationToken);
        await contractService.EnsureCustomerCanViewAsync(id, companyId, cancellationToken);
        return Ok(await paymentService.GetContractBillingAsync(id, CurrentUserId(), isCustomer: true, canViewAll: false, cancellationToken));
    }

    // ================================================================ sự cố

    /// <summary>Máy công ty đang thuê (đã giao, chưa trả) để chọn khi báo sự cố.</summary>
    [HttpGet("equipments")]
    public async Task<ActionResult<List<CustomerRentedEquipmentResponse>>> GetRentedEquipments(
        CancellationToken cancellationToken)
    {
        var companyId = await portalService.GetCompanyIdAsync(CurrentUserId(), cancellationToken);
        return Ok(await incidentService.GetRentedEquipmentsAsync(companyId, cancellationToken));
    }

    /// <summary>Vé upload ảnh sự cố (tối đa 5 ảnh jpg/png/webp, mỗi ảnh ≤ 5 MB).</summary>
    [HttpPost("incidents/upload-ticket")]
    public async Task<ActionResult<FileUploadTicket>> CreateIncidentUploadTicket(
        FileUploadRequest request,
        CancellationToken cancellationToken)
    {
        await portalService.GetCompanyIdAsync(CurrentUserId(), cancellationToken);
        return Ok(await incidentService.CreateUploadTicketAsync(request, CurrentUserId(), cancellationToken));
    }

    [HttpPost("incidents/confirm-upload")]
    public async Task<ActionResult<ConfirmFileUploadResponse>> ConfirmIncidentUpload(
        ConfirmFileUploadRequest request,
        CancellationToken cancellationToken)
    {
        return Ok(await incidentService.ConfirmUploadAsync(request, CurrentUserId(), cancellationToken));
    }

    [HttpGet("incidents")]
    public async Task<ActionResult<List<CustomerIncidentResponse>>> GetIncidents(CancellationToken cancellationToken)
    {
        var companyId = await portalService.GetCompanyIdAsync(CurrentUserId(), cancellationToken);
        return Ok(await incidentService.GetIncidentsAsync(companyId, cancellationToken));
    }

    [HttpGet("incidents/{id:guid}")]
    public async Task<ActionResult<CustomerIncidentDetailResponse>> GetIncident(Guid id, CancellationToken cancellationToken)
    {
        var companyId = await portalService.GetCompanyIdAsync(CurrentUserId(), cancellationToken);
        return Ok(await incidentService.GetIncidentAsync(id, companyId, cancellationToken));
    }

    [HttpPost("incidents")]
    public async Task<ActionResult<CustomerIncidentDetailResponse>> CreateIncident(
        CreateCustomerIncidentRequest request,
        CancellationToken cancellationToken)
    {
        var companyId = await portalService.GetCompanyIdAsync(CurrentUserId(), cancellationToken);
        var detail = await incidentService.CreateAsync(request, companyId, CurrentUserId(), cancellationToken);
        return CreatedAtAction(nameof(GetIncident), new { id = detail.Incident.Id }, detail);
    }

    /// <summary>Hủy sự cố khi còn chờ tiếp nhận (OPEN).</summary>
    [HttpPost("incidents/{id:guid}/cancel")]
    public async Task<ActionResult<CustomerIncidentDetailResponse>> CancelIncident(Guid id, CancellationToken cancellationToken)
    {
        var companyId = await portalService.GetCompanyIdAsync(CurrentUserId(), cancellationToken);
        return Ok(await incidentService.CancelAsync(id, companyId, cancellationToken));
    }

    // Claim NameIdentifier do JwtAuthenticationHandler tạo.
    private Guid CurrentUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
