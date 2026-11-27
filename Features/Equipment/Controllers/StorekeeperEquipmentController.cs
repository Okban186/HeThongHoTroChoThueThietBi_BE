using System.Security.Claims;
using HeThongHoTroChoThueThietBi.Common.FileStorage.Abstractions;
using HeThongHoTroChoThueThietBi.Common.FileStorage.Dtos;
using HeThongHoTroChoThueThietBi.Common.FileStorage.Models;
using HeThongHoTroChoThueThietBi.Features.Equipment.DTOs;
using HeThongHoTroChoThueThietBi.Features.Equipment.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HeThongHoTroChoThueThietBi.Features.Equipment.Controllers;

[ApiController]
[Route("api/storekeeper")]
public sealed class StorekeeperEquipmentController(
    StorekeeperEquipmentService service) : ControllerBase
{

    private static readonly FileUploadPolicy EquipmentImageUploadPolicy = new()
    {
        MaxFileCount = 10,
        MaxFileSizeBytes = 5 * 1024 * 1024, // 5 MB / ảnh
        AllowedExtensions = [".jpg", ".jpeg", ".png", ".webp"],
        ResourceType = CloudinaryResourceType.Image,
        Folder = StorageFolder.Equipment
    };
    private Guid CurrentUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet("equipment-models")]
    [Authorize(Roles = EquipmentRoles.StorekeeperTeam)]
    public async Task<ActionResult<List<EquipmentModelDetailResponse>>> GetModels(
        [FromQuery] string? keyword, [FromQuery] Guid? categoryId, [FromQuery] string? status, CancellationToken ct)
        => Ok(await service.GetModelsForStorekeeperAsync(keyword, categoryId, status, ct));

    [HttpGet("equipment-models/{id:guid}")]
    [Authorize(Roles = EquipmentRoles.StorekeeperTeam)]
    public async Task<ActionResult<EquipmentModelDetailResponse>> GetModelDetail(Guid id, CancellationToken ct)
        => Ok(await service.GetModelDetailAsync(id, ct));

    [HttpPost("equipment-models")]
    [Authorize(Roles = EquipmentRoles.LeadStorekeeperOnly)]
    public async Task<ActionResult<EquipmentModelDetailResponse>> CreateModel([FromBody] SaveEquipmentModelRequest req, CancellationToken ct)
    {
        var created = await service.CreateModelAsync(req, ct);
        return CreatedAtAction(nameof(GetModelDetail), new { id = created.Id }, created);
    }

    [HttpGet("equipment-models/import-template")]
    [Authorize(Roles = EquipmentRoles.LeadStorekeeperOnly)]
    public async Task<IActionResult> DownloadModelImportTemplate(CancellationToken ct)
    {
        var bytes = await service.GenerateModelImportTemplateAsync(ct);
        return File(
            bytes,
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            "MauNhap_EquipmentModel.xlsx");
    }

    [HttpPost("equipment-models/import-excel")]
    [Authorize(Roles = EquipmentRoles.LeadStorekeeperOnly)]
    [Consumes("multipart/form-data")]
    public async Task<ActionResult<ImportEquipmentModelsResult>> ImportModelsFromExcel(
        IFormFile file,
        CancellationToken ct)
        => Ok(await service.ImportModelsFromExcelAsync(file, ct));

    [HttpPut("categories/{id:guid}/status")]
    [Authorize(Roles = EquipmentRoles.LeadStorekeeperOnly)]
    public async Task<ActionResult<CategoryResponse>> UpdateCategoryStatus(Guid id, [FromBody] UpdateEntityStatusRequest req, CancellationToken ct)
    => Ok(await service.UpdateCategoryStatusAsync(id, req.Status, ct));

    [HttpPut("equipment-models/{id:guid}/media/{mediaId:guid}/primary")]
    [Authorize(Roles = EquipmentRoles.LeadStorekeeperOnly)]
    public async Task<IActionResult> SetPrimaryMedia(Guid id, Guid mediaId, CancellationToken ct)
    {
        await service.SetPrimaryMediaAsync(id, mediaId, ct);
        return NoContent();
    }

    [HttpPut("equipment-models/{id:guid}")]
    [Authorize(Roles = EquipmentRoles.LeadStorekeeperOnly)]
    public async Task<ActionResult<EquipmentModelDetailResponse>> UpdateModel(Guid id, [FromBody] SaveEquipmentModelRequest req, CancellationToken ct)
        => Ok(await service.UpdateModelAsync(id, req, ct));

    [HttpPut("equipment-models/{id:guid}/status")]
    [Authorize(Roles = EquipmentRoles.LeadStorekeeperOnly)]
    public async Task<ActionResult<EquipmentModelDetailResponse>> UpdateModelStatus(Guid id, [FromBody] UpdateEntityStatusRequest req, CancellationToken ct)
        => Ok(await service.UpdateModelStatusAsync(id, req.Status, ct));

    [HttpPost("equipment-models/upload-ticket")]
    [Authorize(Roles = EquipmentRoles.LeadStorekeeperOnly)]
    public async Task<ActionResult<FileUploadTicket>> CreateUploadTicket([FromBody] FileUploadRequest req, CancellationToken ct)
    => Ok(await service.CreateModelMediaUploadTicketAsync(EquipmentImageUploadPolicy, req, CurrentUserId(), ct));

    [HttpPost("equipment-models/{id:guid}/confirm-media-upload")]
    [Authorize(Roles = EquipmentRoles.LeadStorekeeperOnly)]
    public async Task<ActionResult<ConfirmFileUploadResponse>> ConfirmAndAttachMedia(Guid id, [FromBody] ConfirmFileUploadRequest req, CancellationToken ct)
        => Ok(await service.ConfirmAndAttachModelMediaAsync(id, req, CurrentUserId(), ct));

    [HttpPost("equipment-models/{id:guid}/media")]
    [Authorize(Roles = EquipmentRoles.LeadStorekeeperOnly)]
    public async Task<ActionResult<EquipmentModelMediaResponse>> AttachMedia(Guid id, [FromBody] AttachEquipmentModelMediaRequest req, CancellationToken ct)
        => Ok(await service.AttachMediaAsync(id, req, ct));

    [HttpDelete("equipment-models/{id:guid}/media/{mediaId:guid}")]
    [Authorize(Roles = EquipmentRoles.LeadStorekeeperOnly)]
    public async Task<IActionResult> DeleteMedia(Guid id, Guid mediaId, CancellationToken ct)
    {
        await service.DeleteMediaAsync(id, mediaId, ct);
        return NoContent();
    }

    [HttpGet("equipments")]
    [Authorize(Roles = EquipmentRoles.StorekeeperTeam)]
    public async Task<ActionResult<PagedResponse<EquipmentItemResponse>>> GetEquipments(
    [FromQuery] Guid? modelId,
    [FromQuery] Guid? warehouseId,
    [FromQuery] string? status,
    [FromQuery] string? condition,
    [FromQuery] string? keyword,
    [FromQuery] int page = 1,
    [FromQuery] int pageSize = 20,
    CancellationToken ct = default)
    => Ok(await service.GetEquipmentsAsync(modelId, warehouseId, status, condition, keyword, page, pageSize, ct));

    [HttpPost("equipments")]
    [Authorize(Roles = EquipmentRoles.LeadStorekeeperOnly)]
    public async Task<ActionResult<EquipmentItemResponse>> CreateEquipment([FromBody] CreateEquipmentRequest req, CancellationToken ct)
        => Ok(await service.CreateEquipmentAsync(req, ct));

    [HttpPost("equipments/batch")]
    [Authorize(Roles = EquipmentRoles.LeadStorekeeperOnly)]
    public async Task<ActionResult<List<EquipmentItemResponse>>> BatchCreateEquipments([FromBody] BatchCreateEquipmentRequest req, CancellationToken ct)
        => Ok(await service.BatchCreateEquipmentsAsync(req, ct));

    [HttpGet("equipments/{id:guid}/history")]
    [Authorize(Roles = "LEAD_STOREKEEPER,STOREKEEPER,MANAGER,TECHNICIAN")]
    public async Task<ActionResult<EquipmentLifecycleHistoryResponse>> GetEquipmentHistory(Guid id, CancellationToken ct)
        => Ok(await service.GetEquipmentHistoryAsync(id, ct));

    [HttpPut("equipments/{id:guid}")]
    [Authorize(Roles = EquipmentRoles.LeadStorekeeperOnly)]
    public async Task<ActionResult<EquipmentItemResponse>> UpdateEquipment(Guid id, [FromBody] UpdateEquipmentRequest req, CancellationToken ct)
        => Ok(await service.UpdateEquipmentAsync(id, req, ct));

    // ===== CATEGORIES =====
    [HttpGet("categories")]
    [Authorize(Roles = EquipmentRoles.CatalogReaders)]
    public async Task<ActionResult<List<CategoryResponse>>> GetCategories([FromQuery] string? keyword, [FromQuery] bool includeInactive, CancellationToken ct)
        => Ok(await service.GetCategoriesAsync(keyword, includeInactive, ct));

    [HttpPost("categories")]
    [Authorize(Roles = EquipmentRoles.LeadStorekeeperOnly)]
    public async Task<ActionResult<CategoryResponse>> CreateCategory([FromBody] SaveCategoryRequest req, CancellationToken ct)
        => Ok(await service.CreateCategoryAsync(req, ct));

    [HttpPut("categories/{id:guid}")]
    [Authorize(Roles = EquipmentRoles.LeadStorekeeperOnly)]
    public async Task<ActionResult<CategoryResponse>> UpdateCategory(Guid id, [FromBody] SaveCategoryRequest req, CancellationToken ct)
        => Ok(await service.UpdateCategoryAsync(id, req, ct));

    [HttpGet("warehouses")]
    [Authorize(Roles = EquipmentRoles.CatalogReaders)]
    public async Task<ActionResult<List<WarehouseResponse>>> GetWarehouses([FromQuery] string? keyword, [FromQuery] bool includeInactive, CancellationToken ct)
        => Ok(await service.GetWarehousesAsync(keyword, includeInactive, ct));

    [HttpPost("warehouses")]
    [Authorize(Roles = "ADMIN")]
    public async Task<ActionResult<WarehouseResponse>> CreateWarehouse([FromBody] SaveWarehouseRequest req, CancellationToken ct)
        => Ok(await service.CreateWarehouseAsync(req, ct));

    [HttpPut("warehouses/{id:guid}")]
    [Authorize(Roles = "ADMIN")]
    public async Task<ActionResult<WarehouseResponse>> UpdateWarehouse(Guid id, [FromBody] SaveWarehouseRequest req, CancellationToken ct)
        => Ok(await service.UpdateWarehouseAsync(id, req, ct));

    [HttpGet("equipment-models/{modelId:guid}/next-serial")]
    [Authorize(Roles = EquipmentRoles.LeadStorekeeperOnly)]
    public async Task<ActionResult<NextSerialPreviewResponse>> GetNextSerialPreview(
    Guid modelId,
    CancellationToken ct)
    => Ok(await service.GetNextSerialPreviewAsync(modelId, ct));

    [HttpPost("equipments/auto-generate")]
    [Authorize(Roles = EquipmentRoles.LeadStorekeeperOnly)]
    public async Task<ActionResult<List<EquipmentItemResponse>>> AutoGenerateEquipments(
        [FromBody] AutoGenerateEquipmentsRequest req,
        CancellationToken ct)
        => Ok(await service.AutoGenerateEquipmentsAsync(req, ct));
}