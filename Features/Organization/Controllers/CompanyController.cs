using System.Security.Claims;
using HeThongHoTroChoThueThietBi.Features.Organization.DTOs;
using HeThongHoTroChoThueThietBi.Features.Organization.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HeThongHoTroChoThueThietBi.Features.Organization.Controllers;

/// <summary>
/// Công ty khách hàng. Sales và Manager được xem; chỉ Sales được tạo/sửa.
/// </summary>
[ApiController]
[Route("api/companies")]
[Authorize(Roles = "SALES,MANAGER")]
public sealed class CompanyController(CompanyService companyService) : ControllerBase
{
    /// <summary>Danh sách công ty. mine=true: chỉ công ty do mình tạo.</summary>
    [HttpGet]
    public async Task<ActionResult<List<CompanyResponse>>> GetCompanies(
        [FromQuery] string? keyword,
        [FromQuery] string? status,
        [FromQuery] bool mine,
        CancellationToken cancellationToken)
    {
        return Ok(await companyService.GetCompaniesAsync(keyword, status, mine, CurrentUserId(), cancellationToken));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<CompanyDetailResponse>> GetById(Guid id, CancellationToken cancellationToken)
    {
        return Ok(await companyService.GetDetailAsync(id, CurrentUserId(), cancellationToken));
    }

    /// <summary>Tạo công ty. Mã công ty tự sinh (COMP-NNNN), người tạo là Sales đang đăng nhập.</summary>
    [HttpPost]
    [Authorize(Roles = "SALES")]
    public async Task<ActionResult<CompanyDetailResponse>> Create(
        [FromBody] CreateCompanyRequest request,
        CancellationToken cancellationToken)
    {
        var detail = await companyService.CreateAsync(request, CurrentUserId(), cancellationToken);

        return CreatedAtAction(nameof(GetById), new { id = detail.Company.Id }, detail);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Roles = "SALES")]
    public async Task<ActionResult<CompanyDetailResponse>> Update(
        Guid id,
        [FromBody] UpdateCompanyRequest request,
        CancellationToken cancellationToken)
    {
        return Ok(await companyService.UpdateAsync(id, request, CurrentUserId(), cancellationToken));
    }

    [HttpPut("{id:guid}/status")]
    [Authorize(Roles = "SALES")]
    public async Task<ActionResult<CompanyDetailResponse>> UpdateStatus(
        Guid id,
        [FromBody] UpdateCompanyStatusRequest request,
        CancellationToken cancellationToken)
    {
        return Ok(await companyService.UpdateStatusAsync(id, request, CurrentUserId(), cancellationToken));
    }

    /// <summary>Tạo tài khoản đăng nhập (role COMPANY) cho người của công ty.</summary>
    [HttpPost("{id:guid}/accounts")]
    [Authorize(Roles = "SALES")]
    public async Task<ActionResult<CompanyDetailResponse>> CreateAccount(
        Guid id,
        [FromBody] CreateCompanyAccountRequest request,
        CancellationToken cancellationToken)
    {
        return Ok(await companyService.CreateAccountAsync(id, request, CurrentUserId(), cancellationToken));
    }

    // Claim NameIdentifier do JwtAuthenticationHandler tạo.
    private Guid CurrentUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
