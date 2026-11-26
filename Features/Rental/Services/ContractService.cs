using HeThongHoTroChoThueThietBi.Common.Exceptions;
using HeThongHoTroChoThueThietBi.Common.FileStorage.Abstractions;
using HeThongHoTroChoThueThietBi.Common.FileStorage.Dtos;
using HeThongHoTroChoThueThietBi.Common.FileStorage.Extensions;
using HeThongHoTroChoThueThietBi.Common.FileStorage.Models;
using HeThongHoTroChoThueThietBi.Common.Persistence;
using HeThongHoTroChoThueThietBi.Features.Equipment.Services;
using HeThongHoTroChoThueThietBi.Features.Finance.Entities;
using HeThongHoTroChoThueThietBi.Features.Finance.Enums;
using HeThongHoTroChoThueThietBi.Features.Rental.DTOs;
using HeThongHoTroChoThueThietBi.Features.Rental.Entities;
using HeThongHoTroChoThueThietBi.Features.Rental.Enums;
using HeThongHoTroChoThueThietBi.Infrastructure.Persistence;
using HeThongHoTroChoThueThietBi.Storage.Entities;
using Microsoft.EntityFrameworkCore;
using static HeThongHoTroChoThueThietBi.Features.Rental.Services.RentalPricing;
using ContractServiceEntity = HeThongHoTroChoThueThietBi.Features.Rental.Entities.RentalContractService;

namespace HeThongHoTroChoThueThietBi.Features.Rental.Services;

/// <summary>
/// Nghiệp vụ hợp đồng thuê.
/// (Tên "ContractService" thay vì "RentalContractService" vì entity dịch vụ hợp đồng
/// rental_contract_services đã dùng tên RentalContractService.)
///
/// Luồng:
///   Sales tạo từ phiếu APPROVED → DRAFT (phiếu chuyển CONVERTED)
///   Sales gửi duyệt            → PENDING_APPROVAL
///   Manager duyệt / từ chối    → PENDING_SIGNATURE / REJECTED (Sales sửa rồi gửi lại)
///   Sales xuất PDF từ mẫu, hai bên ký, Sales tải bản PDF đã ký → SIGNED
///   Sales hủy (trước khi ký)   → CANCELLED
///
/// Quy tắc:
/// - Thời gian thuê, model, số lượng lấy theo phiếu (tối thiểu 3 ngày).
/// - Loại thuê, đơn vị tính và đơn giá do hệ thống quyết định theo số ngày và giá niêm yết
///   của model (xem <see cref="RentalPricing"/>); Sales chỉ chọn tiền cọc và dịch vụ.
///   Đơn giá được lưu lại tại thời điểm tạo/sửa, giá model đổi sau đó không ảnh hưởng hợp đồng.
/// - Tiền dòng = số lượng × đơn giá × số đơn vị (ngày, hoặc tháng = ceil(ngày / 30)).
///   Tổng = Σ dòng thiết bị + Σ dịch vụ do bên cho thuê thực hiện.
/// - Giấy tờ bắt buộc: CCCD mặt trước, mặt sau, giấy phép kinh doanh; giấy ủy quyền nếu người ký
///   là người được ủy quyền. File phải do chính Sales upload và chưa dùng cho hợp đồng khác.
///   (Schema chỉ có ID_CARD/AUTHORIZATION_LETTER/OTHER: mặt trước/sau phân biệt bằng display_order,
///   giấy phép kinh doanh là OTHER kèm ghi chú.)
/// - Sales chỉ thấy hợp đồng do mình tạo; chỉ người tạo được sửa/gửi duyệt/tải bản ký/hủy.
///   Manager xem tất cả và duyệt/từ chối.
/// - Mỗi lần đổi trạng thái ghi contract_status_history.
/// </summary>
public sealed class ContractService(
    AppDbContext db,
    EquipmentAvailabilityService availabilityService,
    IFileUploadService uploadService,
    ContractPdfGenerator pdfGenerator,
    ShippingFeeService shippingFeeService)
{
    private const string CodePrefix = "HD";
    private const decimal DefaultDepositRate = 0.2m;
    private const string BusinessLicenseNote = "BUSINESS_LICENSE";

    private static readonly FileUploadPolicy DocumentUploadPolicy = new()
    {
        MaxFileCount = 6,
        MaxFileSizeBytes = 5 * 1024 * 1024,
        AllowedExtensions = [".jpg", ".jpeg", ".png", ".webp"],
        ResourceType = CloudinaryResourceType.Image,
        Folder = StorageFolder.Contract,
    };

    // Bản hợp đồng đã ký: chỉ nhận 1 file PDF. Cloudinary xếp PDF vào loại "image" nên
    // allowed_formats được Cloudinary kiểm tra thật (loại "raw" thì không kiểm tra định dạng).
    private static readonly FileUploadPolicy SignedContractUploadPolicy = new()
    {
        MaxFileCount = 1,
        MaxFileSizeBytes = 10 * 1024 * 1024,
        AllowedExtensions = [".pdf"],
        ResourceType = CloudinaryResourceType.Image,
        Folder = StorageFolder.Contract,
    };

    // storage_files.content_type lưu định dạng Cloudinary trả về (jpg, png, pdf, ...).
    private static readonly string[] DocumentFormats = ["jpg", "jpeg", "png", "webp"];
    private static readonly string[] SignedContractFormats = ["pdf"];

    private static readonly string[] ServiceTypes = ["DELIVERY", "LOADING", "UNLOADING", "OTHER"];
    private static readonly string[] ServiceProviders = ["RENTAL_COMPANY", "CUSTOMER"];
    private static readonly string[] RepresentativeTypes = ["LEGAL_REPRESENTATIVE", "AUTHORIZED_REPRESENTATIVE"];

    // ================================================================ đọc

    public async Task<List<RentalContractResponse>> GetContractsAsync(
        string? keyword,
        string? status,
        Guid currentUserId,
        bool canViewAll,
        CancellationToken cancellationToken = default)
    {
        var query =
            from contract in db.RentalContracts.AsNoTracking()
            join company in db.Companies.AsNoTracking() on contract.CompanyId equals company.Id
            select new { contract, company };

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var pattern = $"%{keyword.Trim()}%";

            query = query.Where(x =>
                EF.Functions.ILike(x.contract.ContractCode, pattern) ||
                EF.Functions.ILike(x.company.Name, pattern) ||
                EF.Functions.ILike(x.company.CompanyCode, pattern));
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            var contractStatus = ParseStatus(status);
            query = query.Where(x => x.contract.Status == contractStatus);
        }

        if (!canViewAll)
            query = query.Where(x => x.contract.CreatedBy == currentUserId);

        var contracts = await query
            .OrderByDescending(x => x.contract.CreatedAt)
            .Select(x => x.contract)
            .ToListAsync(cancellationToken);

        return await ToResponsesAsync(contracts, currentUserId, cancellationToken);
    }

    /// <summary>Chi tiết theo quyền người xem: Sales mở hợp đồng của người khác nhận 404.</summary>
    public async Task<RentalContractDetailResponse> GetDetailForViewerAsync(
        Guid id,
        Guid currentUserId,
        bool canViewAll,
        CancellationToken cancellationToken = default)
    {
        if (!canViewAll)
        {
            var contract = await FindAsync(id, tracking: false, cancellationToken);

            if (contract.CreatedBy != currentUserId)
                throw NotFound();
        }

        return await GetDetailAsync(id, currentUserId, cancellationToken);
    }

    /// <summary>
    /// Xuất hợp đồng PDF từ mẫu để in và ký. Hợp đồng chưa được duyệt vẫn xuất được để xem trước
    /// nhưng có dòng "BẢN NHÁP"; hợp đồng đã hủy thì không xuất.
    /// </summary>
    public async Task<(string FileName, byte[] Content)> ExportPdfAsync(
        Guid id,
        Guid currentUserId,
        bool canViewAll,
        CancellationToken cancellationToken = default)
    {
        var detail = await GetDetailForViewerAsync(id, currentUserId, canViewAll, cancellationToken);
        return await BuildPdfAsync(detail, cancellationToken);
    }

    private async Task<(string FileName, byte[] Content)> BuildPdfAsync(
        RentalContractDetailResponse detail,
        CancellationToken cancellationToken)
    {
        var contract = detail.Contract;

        if (contract.Status == EnumName.ToDatabaseValue(RentalContractStatus.Cancelled))
        {
            throw new AppException(
                "CONTRACT_CANCELLED",
                "Hợp đồng đã hủy, không thể xuất PDF.",
                StatusCodes.Status409Conflict);
        }

        var company = await db.Companies
            .AsNoTracking()
            .Where(c => c.Id == contract.CompanyId)
            .Select(c => new { c.TaxCode, c.Address })
            .SingleAsync(cancellationToken);

        var notApprovedYet = new[]
        {
            RentalContractStatus.Draft,
            RentalContractStatus.PendingApproval,
            RentalContractStatus.Rejected,
        }.Select(EnumName.ToDatabaseValue).Contains(contract.Status);

        var vietnamToday = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7));

        var content = pdfGenerator.Generate(new ContractPdfData(
            contract.ContractCode,
            notApprovedYet,
            vietnamToday,
            contract.StartDate,
            contract.EndDate,
            RentalDays(contract.StartDate, contract.EndDate),
            contract.RentalType,
            detail.DeliveryAddress,
            detail.DeliveryNote,
            detail.Note,
            contract.CompanyName,
            company.TaxCode,
            company.Address,
            detail.Representative,
            detail.Items,
            detail.Services,
            contract.ContractTotalAmount,
            contract.DepositRequiredAmount));

        return ($"{contract.ContractCode}.pdf", content);
    }

    // ================================================================ khách hàng (COMPANY)

    // Giai đoạn nội bộ, khách hàng không thấy: soạn, chờ duyệt, bị từ chối.
    private static readonly RentalContractStatus[] InternalStatuses =
    [
        RentalContractStatus.Draft,
        RentalContractStatus.PendingApproval,
        RentalContractStatus.Rejected,
    ];

    /// <summary>
    /// Hợp đồng khách hàng được thấy: của công ty mình, đã qua bước duyệt
    /// (hợp đồng bị hủy khi còn nội bộ thì không hiện).
    /// </summary>
    private IQueryable<RentalContract> CustomerVisible(Guid companyId) =>
        db.RentalContracts.AsNoTracking().Where(c =>
            c.CompanyId == companyId &&
            !InternalStatuses.Contains(c.Status) &&
            (c.Status != RentalContractStatus.Cancelled || c.ApprovedAt != null));

    public async Task<List<RentalContractResponse>> GetCompanyContractsAsync(
        Guid companyId,
        string? status,
        Guid currentUserId,
        CancellationToken cancellationToken = default)
    {
        var query = CustomerVisible(companyId);

        if (!string.IsNullOrWhiteSpace(status))
        {
            var contractStatus = ParseStatus(status);
            query = query.Where(c => c.Status == contractStatus);
        }

        var contracts = await query
            .OrderByDescending(c => c.StartDate)
            .ThenByDescending(c => c.CreatedAt)
            .ToListAsync(cancellationToken);

        return (await ToResponsesAsync(contracts, currentUserId, cancellationToken))
            .Select(ForCustomer)
            .ToList();
    }

    /// <summary>Khách chỉ được xem hợp đồng của công ty mình và đã qua bước duyệt; ngược lại 404.</summary>
    public async Task EnsureCustomerCanViewAsync(
        Guid id,
        Guid companyId,
        CancellationToken cancellationToken = default)
    {
        if (!await CustomerVisible(companyId).AnyAsync(c => c.Id == id, cancellationToken))
            throw NotFound();
    }

    /// <summary>Chi tiết cho khách hàng: hợp đồng công ty khác hoặc còn nội bộ trả 404.</summary>
    public async Task<RentalContractDetailResponse> GetCompanyContractDetailAsync(
        Guid id,
        Guid companyId,
        Guid currentUserId,
        CancellationToken cancellationToken = default)
    {
        await EnsureCustomerCanViewAsync(id, companyId, cancellationToken);

        var detail = await GetDetailAsync(id, currentUserId, cancellationToken);
        var internalStatuses = InternalStatuses.Select(EnumName.ToDatabaseValue).ToHashSet();

        return detail with
        {
            Contract = ForCustomer(detail.Contract),
            StatusHistory = detail.StatusHistory.Where(h => !internalStatuses.Contains(h.Status)).ToList(),
        };
    }

    public async Task<(string FileName, byte[] Content)> ExportCompanyPdfAsync(
        Guid id,
        Guid companyId,
        Guid currentUserId,
        CancellationToken cancellationToken = default)
    {
        var detail = await GetCompanyContractDetailAsync(id, companyId, currentUserId, cancellationToken);
        return await BuildPdfAsync(detail, cancellationToken);
    }

    private static RentalContractResponse ForCustomer(RentalContractResponse contract) =>
        contract with { CanEdit = false, RejectionReason = null };

    public async Task<RentalContractDetailResponse> GetDetailAsync(
        Guid id,
        Guid currentUserId,
        CancellationToken cancellationToken = default)
    {
        var contract = await FindAsync(id, tracking: false, cancellationToken);
        var response = (await ToResponsesAsync([contract], currentUserId, cancellationToken)).Single();
        var days = RentalDays(contract.StartDate, contract.EndDate);

        var items = await (
            from item in db.RentalContractItems.AsNoTracking()
            join model in db.EquipmentModels.AsNoTracking() on item.EquipmentModelId equals model.Id
            where item.RentalContractId == id
            orderby model.Name
            select new { item, model }
        ).ToListAsync(cancellationToken);

        var services = await db.RentalContractServices
            .AsNoTracking()
            .Where(s => s.RentalContractId == id)
            .ToListAsync(cancellationToken);

        var representative = await db.RentalContractRepresentatives
            .AsNoTracking()
            .SingleOrDefaultAsync(r => r.RentalContractId == id, cancellationToken);

        var documents = await (
            from document in db.RentalContractDocuments.AsNoTracking()
            join file in db.StorageFiles.AsNoTracking() on document.StorageFileId equals file.Id
            where document.RentalContractId == id
            orderby document.DocumentType, document.DisplayOrder
            select new { document, file }
        ).ToListAsync(cancellationToken);

        var history = await (
            from h in db.ContractStatusHistory.AsNoTracking()
            join user in db.Users.AsNoTracking() on h.ChangedBy equals user.Id
            where h.RentalContractId == id
            orderby h.ChangedAt
            select new { h, user.FullName }
        ).ToListAsync(cancellationToken);

        string? approvedByName = null;

        if (contract.ApprovedBy is not null)
        {
            approvedByName = await db.Users
                .AsNoTracking()
                .Where(u => u.Id == contract.ApprovedBy)
                .Select(u => u.FullName)
                .SingleOrDefaultAsync(cancellationToken);
        }

        string? signedUrl = null;

        if (contract.ContractFileId is not null)
        {
            var file = await db.StorageFiles
                .AsNoTracking()
                .SingleOrDefaultAsync(f => f.Id == contract.ContractFileId, cancellationToken);

            signedUrl = file is null ? null : FileUrl(file);
        }

        return new RentalContractDetailResponse(
            response,
            contract.DeliveryAddress,
            contract.DeliveryNote,
            contract.Note,
            items.Select(x => ToItemResponse(
                x.item.Id,
                x.model.Id,
                x.model.Code,
                x.model.Name,
                x.model.Unit,
                x.item.Quantity,
                x.item.BillingUnit,
                x.item.AgreedUnitPrice,
                days,
                x.item.Note)).ToList(),
            services.Select(s => new RentalContractServiceResponse(
                s.Id, s.ServiceType, s.ProvidedBy, s.Quantity, s.UnitPrice, s.Amount, s.Note)).ToList(),
            representative is null
                ? null
                : new ContractRepresentativeResponse(
                    representative.FullName,
                    representative.IdNumber,
                    representative.Phone,
                    representative.Email,
                    representative.RepresentativeType),
            documents.Select(x => new ContractDocumentResponse(
                x.document.Id,
                DocumentKind(x.document),
                x.file.Id,
                x.file.OriginalName,
                FileUrl(x.file))).ToList(),
            history.Select(x => new ContractStatusHistoryResponse(
                EnumName.ToDatabaseValue(x.h.Status),
                EnumName.ToDatabaseValue(x.h.PreviousStatus),
                x.FullName,
                x.h.ChangedAt,
                x.h.Note)).ToList(),
            approvedByName,
            contract.ApprovedAt,
            contract.SignedAt,
            signedUrl);
    }

    /// <summary>Giá trị đề xuất để điền sẵn form tạo hợp đồng từ phiếu đã xác nhận.</summary>
    public async Task<RentalContractSuggestionResponse> GetSuggestionAsync(
        Guid reservationId,
        Guid currentUserId,
        CancellationToken cancellationToken = default)
    {
        var reservation = await FindConvertibleReservationAsync(reservationId, currentUserId, tracking: false, cancellationToken);
        var days = RentalDays(reservation.RequestedFrom, reservation.RequestedTo);
        var billingUnit = BillingUnitFor(days);

        var company = await db.Companies
            .AsNoTracking()
            .SingleAsync(c => c.Id == reservation.CompanyId, cancellationToken);

        var items = await (
            from item in db.ReservationItems.AsNoTracking()
            join model in db.EquipmentModels.AsNoTracking() on item.EquipmentModelId equals model.Id
            where item.ReservationId == reservationId
            orderby model.Name
            select new { item, model }
        ).ToListAsync(cancellationToken);

        var lines = items.Select(x => ToItemResponse(
            Guid.Empty,
            x.model.Id,
            x.model.Code,
            x.model.Name,
            x.model.Unit,
            x.item.Quantity,
            billingUnit,
            UnitPriceFor(days, x.model.RentalPriceDaily, x.model.RentalPriceMonthly),
            days,
            x.item.Note)).ToList();

        var total = lines.Sum(l => l.Amount);

        return new RentalContractSuggestionResponse(
            reservation.Id,
            reservation.ReservationCode,
            company.Id,
            company.Name,
            company.Address,
            reservation.RequestedFrom,
            reservation.RequestedTo,
            days,
            RentalTypeFor(days),
            DefaultDepositRate,
            lines,
            total,
            RoundToThousand(total * DefaultDepositRate),
            shippingFeeService.BuildPolicySummary());
    }

    /// <summary>Tính trước phí vận chuyển tự động từ Kho chính tới địa chỉ giao hàng.</summary>
    public async Task<ShippingQuoteResponse> GetShippingQuoteAsync(
        ShippingQuoteRequest request,
        Guid currentUserId,
        bool canViewAll,
        CancellationToken cancellationToken = default)
    {
        decimal itemsTotal;
        int totalQuantity;

        if (request.ReservationId is not null)
        {
            var reservation = await FindConvertibleReservationAsync(
                request.ReservationId.Value, currentUserId, tracking: false, cancellationToken);
            var days = RentalDays(reservation.RequestedFrom, reservation.RequestedTo);
            var billingUnit = BillingUnitFor(days);

            var rows = await (
                from item in db.ReservationItems.AsNoTracking()
                join model in db.EquipmentModels.AsNoTracking() on item.EquipmentModelId equals model.Id
                where item.ReservationId == reservation.Id
                select new { item.Quantity, model.RentalPriceDaily, model.RentalPriceMonthly }
            ).ToListAsync(cancellationToken);

            var units = BillableUnits(billingUnit, days);
            itemsTotal = rows.Sum(r => r.Quantity * UnitPriceFor(days, r.RentalPriceDaily, r.RentalPriceMonthly) * units);
            totalQuantity = rows.Sum(r => r.Quantity);
        }
        else if (request.ContractId is not null)
        {
            var contract = await FindAsync(request.ContractId.Value, tracking: false, cancellationToken);
            if (!canViewAll && contract.CreatedBy != currentUserId)
                throw NotFound();

            var days = RentalDays(contract.StartDate, contract.EndDate);
            var billingUnit = BillingUnitFor(days);
            var units = BillableUnits(billingUnit, days);

            var rows = await (
                from item in db.RentalContractItems.AsNoTracking()
                join model in db.EquipmentModels.AsNoTracking() on item.EquipmentModelId equals model.Id
                where item.RentalContractId == contract.Id
                select new { item.Quantity, model.RentalPriceDaily, model.RentalPriceMonthly }
            ).ToListAsync(cancellationToken);

            itemsTotal = rows.Sum(r => r.Quantity * UnitPriceFor(days, r.RentalPriceDaily, r.RentalPriceMonthly) * units);
            totalQuantity = rows.Sum(r => r.Quantity);
        }
        else
        {
            throw new AppException(
                "SHIPPING_QUOTE_SOURCE_REQUIRED",
                "Cần truyền reservationId hoặc contractId để tính phí vận chuyển.",
                StatusCodes.Status400BadRequest);
        }

        return await shippingFeeService.CalculateAsync(
            request.ProvidedBy,
            request.DeliveryAddress,
            itemsTotal,
            totalQuantity,
            cancellationToken);
    }

    // ================================================================ upload giấy tờ

    public Task<FileUploadTicket> CreateUploadTicketAsync(
        FileUploadRequest request,
        Guid currentUserId,
        CancellationToken cancellationToken = default)
        => uploadService.CreateUploadTicketAsync(DocumentUploadPolicy, request, currentUserId, cancellationToken);

    /// <summary>Vé upload bản hợp đồng đã ký (1 file PDF, tối đa 10 MB).</summary>
    public Task<FileUploadTicket> CreateSignedUploadTicketAsync(
        Guid currentUserId,
        CancellationToken cancellationToken = default)
        => uploadService.CreateUploadTicketAsync(
            SignedContractUploadPolicy,
            new FileUploadRequest { FileCount = 1 },
            currentUserId,
            cancellationToken);

    public Task<ConfirmFileUploadResponse> ConfirmUploadAsync(
        ConfirmFileUploadRequest request,
        Guid currentUserId,
        CancellationToken cancellationToken = default)
        => uploadService.ConfirmUploadAsync(request, currentUserId, cancellationToken);

    // ================================================================ tạo / sửa

    public async Task<RentalContractDetailResponse> CreateAsync(
        CreateRentalContractRequest request,
        Guid currentUserId,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var reservation = await FindConvertibleReservationAsync(
            request.ReservationId!.Value, currentUserId, tracking: true, cancellationToken);

        var reservationItems = await db.ReservationItems
            .AsNoTracking()
            .Where(i => i.ReservationId == reservation.Id)
            .ToListAsync(cancellationToken);

        // Chuyển giữ chỗ từ phiếu sang hợp đồng: khóa model và kiểm tra lại số lượng
        // (loại trừ chính phiếu vì phiếu sẽ chuyển CONVERTED).
        var quantities = reservationItems.ToDictionary(i => i.EquipmentModelId, i => i.Quantity);
        await availabilityService.LockModelsAsync(quantities.Keys.ToList(), cancellationToken);

        var availability = await availabilityService.GetAvailabilityAsync(
            quantities.Keys.ToList(),
            reservation.RequestedFrom,
            reservation.RequestedTo,
            excludeReservationId: reservation.Id,
            cancellationToken: cancellationToken);

        if (quantities.Any(q => q.Value > availability[q.Key].AvailableQuantity))
        {
            throw new AppException(
                "INSUFFICIENT_EQUIPMENT",
                "Số lượng thiết bị khả dụng đã thay đổi, không đủ cho phiếu này. Hãy mở lại phiếu để điều chỉnh.",
                StatusCodes.Status409Conflict);
        }

        var now = DateTimeOffset.UtcNow;

        var contract = new RentalContract
        {
            Id = Guid.CreateVersion7(),
            ContractCode = await GenerateCodeAsync(cancellationToken),
            ReservationId = reservation.Id,
            CompanyId = reservation.CompanyId,
            StartDate = reservation.RequestedFrom,
            EndDate = reservation.RequestedTo,
            Status = RentalContractStatus.Draft,
            CreatedBy = currentUserId,
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.RentalContracts.Add(contract);

        await ApplyContentAsync(contract, request, reservationItems, currentUserId, isNew: true, cancellationToken);

        reservation.Status = ReservationStatus.Converted;
        reservation.UpdatedAt = now;

        AddHistory(contract, RentalContractStatus.Draft, RentalContractStatus.Draft, currentUserId,
            $"Tạo hợp đồng từ phiếu {reservation.ReservationCode}");

        await SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return await GetDetailAsync(contract.Id, currentUserId, cancellationToken);
    }

    public async Task<RentalContractDetailResponse> UpdateAsync(
        Guid id,
        UpdateRentalContractRequest request,
        Guid currentUserId,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var contract = await FindAsync(id, tracking: true, cancellationToken);
        EnsureOwner(contract, currentUserId);
        EnsureStatus(contract, RentalContractStatus.Draft, RentalContractStatus.Rejected);

        // Model và số lượng giữ nguyên theo hợp đồng hiện tại.
        var currentItems = await db.RentalContractItems
            .AsNoTracking()
            .Where(i => i.RentalContractId == id)
            .Select(i => new ReservationItem { EquipmentModelId = i.EquipmentModelId, Quantity = i.Quantity })
            .ToListAsync(cancellationToken);

        await ApplyContentAsync(contract, request, currentItems, currentUserId, isNew: false, cancellationToken);
        contract.UpdatedAt = DateTimeOffset.UtcNow;

        await SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return await GetDetailAsync(id, currentUserId, cancellationToken);
    }

    // ================================================================ trạng thái

    /// <summary>Sales gửi duyệt (DRAFT/REJECTED → PENDING_APPROVAL).</summary>
    public async Task<RentalContractDetailResponse> SubmitAsync(Guid id, Guid currentUserId, CancellationToken cancellationToken = default)
    {
        var contract = await FindAsync(id, tracking: true, cancellationToken);
        EnsureOwner(contract, currentUserId);
        EnsureStatus(contract, RentalContractStatus.Draft, RentalContractStatus.Rejected);

        contract.RejectionReason = null;
        ChangeStatus(contract, RentalContractStatus.PendingApproval, currentUserId, "Gửi duyệt");

        await SaveChangesAsync(cancellationToken);
        return await GetDetailAsync(id, currentUserId, cancellationToken);
    }

    /// <summary>Manager duyệt (PENDING_APPROVAL → PENDING_SIGNATURE).</summary>
    public async Task<RentalContractDetailResponse> ApproveAsync(Guid id, Guid currentUserId, CancellationToken cancellationToken = default)
    {
        var contract = await FindAsync(id, tracking: true, cancellationToken);
        EnsureStatus(contract, RentalContractStatus.PendingApproval);

        contract.ApprovedBy = currentUserId;
        contract.ApprovedAt = DateTimeOffset.UtcNow;
        ChangeStatus(contract, RentalContractStatus.PendingSignature, currentUserId, "Đã duyệt");

        await SaveChangesAsync(cancellationToken);
        return await GetDetailAsync(id, currentUserId, cancellationToken);
    }

    /// <summary>Manager từ chối (PENDING_APPROVAL → REJECTED) kèm lý do.</summary>
    public async Task<RentalContractDetailResponse> RejectAsync(
        Guid id,
        RejectRentalContractRequest request,
        Guid currentUserId,
        CancellationToken cancellationToken = default)
    {
        var contract = await FindAsync(id, tracking: true, cancellationToken);
        EnsureStatus(contract, RentalContractStatus.PendingApproval);

        var reason = request.Reason.Trim();
        contract.RejectionReason = reason;
        ChangeStatus(contract, RentalContractStatus.Rejected, currentUserId, reason);

        await SaveChangesAsync(cancellationToken);
        return await GetDetailAsync(id, currentUserId, cancellationToken);
    }

    /// <summary>Sales tải bản hợp đồng đã ký (PENDING_SIGNATURE → SIGNED) và tự động sinh các đợt tiền thuê.</summary>
    public async Task<RentalContractDetailResponse> MarkSignedAsync(
        Guid id,
        MarkRentalContractSignedRequest request,
        Guid currentUserId,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var contract = await FindAsync(id, tracking: true, cancellationToken);
        EnsureOwner(contract, currentUserId);
        EnsureStatus(contract, RentalContractStatus.PendingSignature);

        await EnsureFilesUsableAsync(
            [request.ContractFileId!.Value], SignedContractFormats, currentUserId, contract.Id, cancellationToken);

        var now = DateTimeOffset.UtcNow;
        contract.ContractFileId = request.ContractFileId;
        contract.SignedAt = now;
        contract.EffectiveFrom = contract.StartDate;
        contract.EffectiveTo = contract.EndDate;
        ChangeStatus(contract, RentalContractStatus.Signed, currentUserId, "Đã ký hợp đồng");

        await GenerateRentalChargesIfNeededAsync(contract, currentUserId, cancellationToken);

        await SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await GetDetailAsync(id, currentUserId, cancellationToken);
    }

    /// <summary>
    /// Tự động sinh các đợt thanh toán tiền thuê (rental_charges) khi hợp đồng chuyển SIGNED:
    /// - SHORT_TERM: 1 đợt duy nhất (tiền thuê + phí vận chuyển).
    /// - LONG_TERM: chia đều theo số tháng làm tròn lên (30 ngày/tháng); đợt cuối giữ nguyên phần ngày dư;
    ///   phí vận chuyển gộp vào đợt 1 để tổng các đợt khớp 100% ContractTotalAmount.
    /// </summary>
    private async Task GenerateRentalChargesIfNeededAsync(
        RentalContract contract,
        Guid createdBy,
        CancellationToken cancellationToken)
    {
        var alreadyExists = await db.RentalCharges
            .AsNoTracking()
            .AnyAsync(c => c.RentalContractId == contract.Id && c.ChargeType == "RENTAL", cancellationToken);

        if (alreadyExists)
            return;

        var days = RentalDays(contract.StartDate, contract.EndDate);
        var items = await db.RentalContractItems
            .AsNoTracking()
            .Where(i => i.RentalContractId == contract.Id)
            .ToListAsync(cancellationToken);

        var shippingFee = await db.RentalContractServices
            .AsNoTracking()
            .Where(s => s.RentalContractId == contract.Id && s.ProvidedBy == "RENTAL_COMPANY")
            .SumAsync(s => (decimal?)s.Amount, cancellationToken) ?? 0m;

        var itemsTotal = items.Sum(i => i.Quantity * i.AgreedUnitPrice * BillableUnits(i.BillingUnit, days));
        var now = DateTimeOffset.UtcNow;

        if (contract.RentalType == ShortTerm)
        {
            var amount = itemsTotal + shippingFee;
            db.RentalCharges.Add(new RentalCharge
            {
                Id = Guid.CreateVersion7(),
                SourceType = "CONTRACT",
                RentalContractId = contract.Id,
                ChargeType = "RENTAL",
                Description = shippingFee > 0
                    ? $"Tiền thuê trọn gói ({days} ngày) + Phí vận chuyển"
                    : $"Tiền thuê trọn gói ({days} ngày)",
                Quantity = 1,
                UnitPrice = amount,
                Amount = amount,
                PeriodStart = contract.StartDate,
                PeriodEnd = contract.EndDate,
                DueDate = contract.EndDate.AddDays(7),
                PaidAmount = 0,
                Status = RentalChargeStatus.Unpaid,
                CreatedBy = createdBy,
                CreatedAt = now,
                Note = shippingFee > 0 ? $"Đã gộp phí vận chuyển {shippingFee:N0} đ" : null,
            });
            return;
        }

        // LONG_TERM: chia theo chu kỳ 30 ngày (số đợt = BillableUnits MONTHLY = ceil(days / 30)).
        var totalMonths = Math.Max(1, BillableUnits(Monthly, days));
        var baseMonthlyAmount = Math.Floor(itemsTotal / totalMonths);

        for (var i = 0; i < totalMonths; i++)
        {
            var installmentNo = i + 1;
            var periodStart = contract.StartDate.AddDays(i * 30);
            var periodEnd = installmentNo == totalMonths
                ? contract.EndDate
                : contract.StartDate.AddDays(installmentNo * 30 - 1);

            var rentPart = installmentNo == totalMonths
                ? itemsTotal - baseMonthlyAmount * (totalMonths - 1)
                : baseMonthlyAmount;

            var shippingPart = installmentNo == 1 ? shippingFee : 0m;
            var amount = rentPart + shippingPart;
            var dueDate = periodStart.AddDays(5);

            db.RentalCharges.Add(new RentalCharge
            {
                Id = Guid.CreateVersion7(),
                SourceType = "CONTRACT",
                RentalContractId = contract.Id,
                ChargeType = "RENTAL",
                Description = shippingPart > 0
                    ? $"Tiền thuê đợt {installmentNo}/{totalMonths} (gồm phí vận chuyển)"
                    : $"Tiền thuê đợt {installmentNo}/{totalMonths}",
                Quantity = 1,
                UnitPrice = amount,
                Amount = amount,
                PeriodStart = periodStart,
                PeriodEnd = periodEnd,
                DueDate = dueDate,
                PaidAmount = 0,
                Status = RentalChargeStatus.Unpaid,
                CreatedBy = createdBy,
                CreatedAt = now,
                Note = shippingPart > 0 ? $"Gồm tiền thuê {rentPart:N0} đ + phí vận chuyển {shippingPart:N0} đ" : null,
            });
        }
    }

    /// <summary>Sales hủy hợp đồng trước khi ký. Thiết bị được nhả ra theo công thức khả dụng.</summary>
    public async Task<RentalContractDetailResponse> CancelAsync(
        Guid id,
        CancelRentalContractRequest request,
        Guid currentUserId,
        CancellationToken cancellationToken = default)
    {
        var contract = await FindAsync(id, tracking: true, cancellationToken);
        EnsureOwner(contract, currentUserId);
        EnsureStatus(
            contract,
            RentalContractStatus.Draft,
            RentalContractStatus.Rejected,
            RentalContractStatus.PendingApproval,
            RentalContractStatus.PendingSignature);

        var reason = NormalizeOptional(request.Reason);
        var now = DateTimeOffset.UtcNow;

        contract.CancellationReason = reason;
        contract.CancelledAt = now;
        contract.CancelledBy = currentUserId;
        ChangeStatus(contract, RentalContractStatus.Cancelled, currentUserId, reason ?? "Hủy hợp đồng");

        await SaveChangesAsync(cancellationToken);
        return await GetDetailAsync(id, currentUserId, cancellationToken);
    }

    // ================================================================ helpers

    /// <summary>Ghi nội dung hợp đồng (dòng thiết bị, dịch vụ, người đại diện, giấy tờ, tiền).</summary>
    private async Task ApplyContentAsync(
        RentalContract contract,
        RentalContractContent content,
        List<ReservationItem> sourceItems,
        Guid currentUserId,
        bool isNew,
        CancellationToken cancellationToken)
    {
        var representativeType = RequireOneOf(
            content.Representative!.RepresentativeType, RepresentativeTypes,
            "representative.representativeType", "Tư cách người ký không hợp lệ.");

        // Mỗi model của phiếu đúng một dòng, không thêm model khác.
        var requestedItems = content.Items.ToDictionary(i => i.EquipmentModelId!.Value);
        var sourceModelIds = sourceItems.Select(i => i.EquipmentModelId).ToHashSet();

        if (requestedItems.Count != content.Items.Count || !requestedItems.Keys.ToHashSet().SetEquals(sourceModelIds))
        {
            throw new AppException(
                "CONTRACT_ITEMS_MISMATCH",
                "Danh sách thiết bị phải khớp với phiếu giữ chỗ (mỗi model một dòng).",
                StatusCodes.Status400BadRequest,
                "items");
        }

        var documents = content.Documents!;
        var isAuthorized = representativeType == "AUTHORIZED_REPRESENTATIVE";

        if (isAuthorized && documents.AuthorizationLetterFileId is null)
        {
            throw new AppException(
                "AUTHORIZATION_LETTER_REQUIRED",
                "Người ký là người được ủy quyền: vui lòng tải lên giấy ủy quyền.",
                StatusCodes.Status400BadRequest,
                "documents.authorizationLetterFileId");
        }

        var fileIds = new List<Guid>
        {
            documents.IdCardFrontFileId!.Value,
            documents.IdCardBackFileId!.Value,
            documents.BusinessLicenseFileId!.Value,
        };

        if (isAuthorized)
            fileIds.Add(documents.AuthorizationLetterFileId!.Value);

        if (fileIds.Distinct().Count() != fileIds.Count)
        {
            throw new AppException(
                "DUPLICATE_DOCUMENT_FILE",
                "Mỗi loại giấy tờ cần một ảnh riêng.",
                StatusCodes.Status400BadRequest,
                "documents");
        }

        await EnsureFilesUsableAsync(fileIds, DocumentFormats, currentUserId, contract.Id, cancellationToken);

        // --- dòng thiết bị: đơn vị tính và đơn giá theo số ngày thuê và giá niêm yết của model
        var days = RentalDays(contract.StartDate, contract.EndDate);
        var billingUnit = BillingUnitFor(days);

        var modelIds = sourceItems.Select(i => i.EquipmentModelId).ToList();
        var prices = await db.EquipmentModels
            .AsNoTracking()
            .Where(m => modelIds.Contains(m.Id))
            .ToDictionaryAsync(
                m => m.Id,
                m => UnitPriceFor(days, m.RentalPriceDaily, m.RentalPriceMonthly),
                cancellationToken);

        if (!isNew)
        {
            db.RentalContractItems.RemoveRange(
                await db.RentalContractItems.Where(i => i.RentalContractId == contract.Id).ToListAsync(cancellationToken));
        }

        decimal itemsTotal = 0;

        foreach (var source in sourceItems)
        {
            var line = requestedItems[source.EquipmentModelId];
            var unitPrice = prices[source.EquipmentModelId];

            itemsTotal += source.Quantity * unitPrice * BillableUnits(billingUnit, days);

            db.RentalContractItems.Add(new RentalContractItem
            {
                Id = Guid.CreateVersion7(),
                RentalContractId = contract.Id,
                EquipmentModelId = source.EquipmentModelId,
                Quantity = source.Quantity,
                BillingUnit = billingUnit,
                AgreedUnitPrice = unitPrice,
                Note = NormalizeOptional(line.Note),
            });
        }

        // --- vận chuyển tự động (khóa không cho Sales tự tạo dịch vụ hay sửa giá vận chuyển)
        if (content.Services.Count > 0)
        {
            throw new AppException(
                "MANUAL_SERVICES_DISABLED",
                "Không cho phép tự thêm dịch vụ hoặc chỉnh tay phí vận chuyển; hệ thống tự động tính phí vận chuyển.",
                StatusCodes.Status400BadRequest,
                "services");
        }

        if (!isNew)
        {
            db.RentalContractServices.RemoveRange(
                await db.RentalContractServices.Where(s => s.RentalContractId == contract.Id).ToListAsync(cancellationToken));
        }

        var totalQuantity = sourceItems.Sum(i => i.Quantity);
        var shippingQuote = await shippingFeeService.CalculateAsync(
            content.DeliveryProvidedBy,
            content.DeliveryAddress,
            itemsTotal,
            totalQuantity,
            cancellationToken);

        var servicesTotal = shippingQuote.ProvidedBy == ShippingFeeService.RentalCompanyProvider
            ? shippingQuote.FinalFee
            : 0m;

        db.RentalContractServices.Add(new ContractServiceEntity
        {
            Id = Guid.CreateVersion7(),
            RentalContractId = contract.Id,
            ServiceType = "DELIVERY",
            ProvidedBy = shippingQuote.ProvidedBy,
            Quantity = 1,
            UnitPrice = servicesTotal,
            Amount = servicesTotal,
            Note = shippingQuote.SummaryNote,
        });

        var total = itemsTotal + servicesTotal;

        if (content.DepositRequiredAmount > total)
        {
            throw new AppException(
                "DEPOSIT_EXCEEDS_TOTAL",
                "Tiền cọc không được lớn hơn tổng giá trị hợp đồng.",
                StatusCodes.Status400BadRequest,
                "depositRequiredAmount");
        }

        // --- người đại diện (mỗi hợp đồng một người)
        var representative = isNew
            ? null
            : await db.RentalContractRepresentatives.SingleOrDefaultAsync(r => r.RentalContractId == contract.Id, cancellationToken);

        if (representative is null)
        {
            representative = new RentalContractRepresentative
            {
                Id = Guid.CreateVersion7(),
                RentalContractId = contract.Id,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.RentalContractRepresentatives.Add(representative);
        }

        var rep = content.Representative!;
        representative.FullName = rep.FullName.Trim();
        representative.IdNumber = rep.IdNumber.Trim();
        representative.Phone = NormalizeOptional(rep.Phone);
        representative.Email = NormalizeOptional(rep.Email);
        representative.RepresentativeType = representativeType;

        // --- giấy tờ
        if (!isNew)
        {
            db.RentalContractDocuments.RemoveRange(
                await db.RentalContractDocuments.Where(d => d.RentalContractId == contract.Id).ToListAsync(cancellationToken));
        }

        AddDocument(contract.Id, representative.Id, "ID_CARD", 1, documents.IdCardFrontFileId!.Value, rep.IdNumber.Trim(), null);
        AddDocument(contract.Id, representative.Id, "ID_CARD", 2, documents.IdCardBackFileId!.Value, rep.IdNumber.Trim(), null);
        AddDocument(contract.Id, null, "OTHER", 1, documents.BusinessLicenseFileId!.Value, null, BusinessLicenseNote);

        if (isAuthorized)
            AddDocument(contract.Id, representative.Id, "AUTHORIZATION_LETTER", 1, documents.AuthorizationLetterFileId!.Value, null, null);

        // --- thông tin chung và tiền
        contract.RentalType = RentalTypeFor(days);
        contract.DeliveryAddress = NormalizeOptional(content.DeliveryAddress);
        contract.DeliveryNote = NormalizeOptional(content.DeliveryNote);
        contract.Note = NormalizeOptional(content.Note);
        contract.ContractTotalAmount = total;
        contract.DepositRequiredAmount = content.DepositRequiredAmount;
    }

    private void AddDocument(Guid contractId, Guid? representativeId, string type, int order, Guid fileId, string? number, string? note)
    {
        db.RentalContractDocuments.Add(new RentalContractDocument
        {
            Id = Guid.CreateVersion7(),
            RentalContractId = contractId,
            RepresentativeId = representativeId,
            DocumentType = type,
            DisplayOrder = order,
            StorageFileId = fileId,
            DocumentNumber = number,
            Note = note,
            CreatedAt = DateTimeOffset.UtcNow,
        });
    }

    /// <summary>File phải do chính người dùng upload và chưa thuộc hợp đồng khác.</summary>
    /// <summary>
    /// File phải do chính Sales upload, đúng định dạng (ảnh cho giấy tờ, PDF cho bản đã ký)
    /// và chưa gắn với hợp đồng khác.
    /// </summary>
    private async Task EnsureFilesUsableAsync(
        List<Guid> fileIds,
        string[] allowedFormats,
        Guid currentUserId,
        Guid contractId,
        CancellationToken cancellationToken)
    {
        var isSignedContract = allowedFormats == SignedContractFormats;
        var field = isSignedContract ? "contractFileId" : "documents";

        var files = await db.StorageFiles
            .AsNoTracking()
            .Where(f => fileIds.Contains(f.Id))
            .Select(f => new { f.Id, f.UploadedBy, f.ContentType })
            .ToListAsync(cancellationToken);

        if (files.Count != fileIds.Distinct().Count() || files.Any(f => f.UploadedBy != currentUserId))
        {
            throw new AppException(
                "DOCUMENT_FILE_INVALID",
                isSignedContract
                    ? "File hợp đồng đã ký không hợp lệ hoặc không phải do bạn tải lên."
                    : "Ảnh giấy tờ không hợp lệ hoặc không phải do bạn tải lên.",
                StatusCodes.Status400BadRequest,
                field);
        }

        if (files.Any(f => !allowedFormats.Contains((f.ContentType ?? "").ToLowerInvariant())))
        {
            throw new AppException(
                "DOCUMENT_FILE_FORMAT_INVALID",
                isSignedContract
                    ? "Bản hợp đồng đã ký phải là file PDF."
                    : "Giấy tờ phải là ảnh (jpg, png, webp).",
                StatusCodes.Status400BadRequest,
                field);
        }

        var usedElsewhere =
            await db.RentalContractDocuments.AnyAsync(
                d => fileIds.Contains(d.StorageFileId) && d.RentalContractId != contractId, cancellationToken) ||
            await db.RentalContracts.AnyAsync(
                c => c.ContractFileId != null && fileIds.Contains(c.ContractFileId.Value) && c.Id != contractId, cancellationToken);

        if (usedElsewhere)
        {
            throw new AppException(
                "DOCUMENT_FILE_IN_USE",
                "File đã được dùng cho hợp đồng khác.",
                StatusCodes.Status409Conflict,
                field);
        }
    }

    private async Task<Reservation> FindConvertibleReservationAsync(
        Guid reservationId,
        Guid currentUserId,
        bool tracking,
        CancellationToken cancellationToken)
    {
        var query = tracking ? db.Reservations : db.Reservations.AsNoTracking();

        var reservation = await query.SingleOrDefaultAsync(r => r.Id == reservationId, cancellationToken)
            ?? throw new AppException("RESERVATION_NOT_FOUND", "Không tìm thấy phiếu giữ chỗ.", StatusCodes.Status404NotFound);

        if (reservation.CreatedBy != currentUserId)
        {
            throw new AppException(
                "RESERVATION_NOT_OWNED",
                "Chỉ nhân viên lập phiếu mới được tạo hợp đồng từ phiếu này.",
                StatusCodes.Status403Forbidden);
        }

        var expired = reservation.ExpiresAt <= DateTimeOffset.UtcNow;

        if (reservation.Status != ReservationStatus.Approved || expired)
        {
            var message = reservation.Status switch
            {
                ReservationStatus.Converted => "Phiếu đã được tạo hợp đồng.",
                ReservationStatus.Pending => "Phiếu chưa được xác nhận.",
                _ when expired => "Phiếu đã hết hạn giữ chỗ.",
                _ => "Chỉ phiếu đã xác nhận mới được tạo hợp đồng.",
            };

            throw new AppException("RESERVATION_NOT_CONVERTIBLE", message, StatusCodes.Status409Conflict);
        }

        // Phiếu lập trước khi có quy định thuê tối thiểu: phải sửa ngày phiếu trước.
        if (RentalDays(reservation.RequestedFrom, reservation.RequestedTo) < MinRentalDays)
        {
            throw new AppException(
                "RENTAL_PERIOD_TOO_SHORT",
                $"Thời gian thuê tối thiểu {MinRentalDays} ngày. Hãy mở lại phiếu, sửa ngày thuê rồi xác nhận lại trước khi tạo hợp đồng.",
                StatusCodes.Status409Conflict);
        }

        return reservation;
    }

    private async Task<RentalContract> FindAsync(Guid id, bool tracking, CancellationToken cancellationToken)
    {
        var query = tracking ? db.RentalContracts : db.RentalContracts.AsNoTracking();

        return await query.SingleOrDefaultAsync(c => c.Id == id, cancellationToken)
               ?? throw NotFound();
    }

    private static AppException NotFound()
        => new("CONTRACT_NOT_FOUND", "Không tìm thấy hợp đồng.", StatusCodes.Status404NotFound);

    private static void EnsureOwner(RentalContract contract, Guid currentUserId)
    {
        if (contract.CreatedBy != currentUserId)
        {
            throw new AppException(
                "CONTRACT_NOT_OWNED",
                "Chỉ nhân viên tạo hợp đồng mới được thao tác.",
                StatusCodes.Status403Forbidden);
        }
    }

    private static void EnsureStatus(RentalContract contract, params RentalContractStatus[] allowed)
    {
        if (allowed.Contains(contract.Status))
            return;

        throw new AppException(
            "INVALID_CONTRACT_STATUS",
            $"Hợp đồng đang ở trạng thái {EnumName.ToDatabaseValue(contract.Status)}, không thể thực hiện thao tác này.",
            StatusCodes.Status409Conflict);
    }

    private void ChangeStatus(RentalContract contract, RentalContractStatus status, Guid changedBy, string? note)
    {
        AddHistory(contract, status, contract.Status, changedBy, note);
        contract.Status = status;
        contract.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private void AddHistory(RentalContract contract, RentalContractStatus status, RentalContractStatus previous, Guid changedBy, string? note)
    {
        db.ContractStatusHistory.Add(new ContractStatusHistory
        {
            Id = Guid.CreateVersion7(),
            RentalContractId = contract.Id,
            Status = status,
            PreviousStatus = previous,
            ChangedBy = changedBy,
            ChangedAt = DateTimeOffset.UtcNow,
            Note = note,
        });
    }

    private static RentalContractItemResponse ToItemResponse(
        Guid id, Guid modelId, string code, string name, string unit,
        int quantity, string billingUnit, decimal price, int days, string? note)
    {
        var units = BillableUnits(billingUnit, days);

        return new RentalContractItemResponse(
            id, modelId, code, name, unit, quantity, billingUnit, price, units, quantity * price * units, note);
    }

    private static decimal RoundToThousand(decimal value) => Math.Round(value / 1000m, MidpointRounding.AwayFromZero) * 1000m;

    private static string DocumentKind(RentalContractDocument document) => document.DocumentType switch
    {
        "ID_CARD" => document.DisplayOrder == 2 ? "ID_CARD_BACK" : "ID_CARD_FRONT",
        "AUTHORIZATION_LETTER" => "AUTHORIZATION_LETTER",
        "OTHER" when document.Note == BusinessLicenseNote => "BUSINESS_LICENSE",
        _ => "OTHER",
    };

    private string FileUrl(StorageFile file)
        => uploadService.GetPrivateImageUrl(
            $"{file.BucketName}/{file.ObjectKey}",
            file.ResourceType.ToCloudinaryNetResourceType(),
            file.ContentType!);

    private static string RequireOneOf(string value, string[] allowed, string field, string message)
    {
        var normalized = value.Trim().ToUpperInvariant();

        return allowed.Contains(normalized)
            ? normalized
            : throw new AppException("INVALID_VALUE", message, StatusCodes.Status400BadRequest, field);
    }

    /// <summary>HD-YYYY-NNNN, đánh số lại mỗi năm.</summary>
    private async Task<string> GenerateCodeAsync(CancellationToken cancellationToken)
    {
        var prefix = $"{CodePrefix}-{EquipmentAvailabilityService.BusinessToday().Year}-";

        var codes = await db.RentalContracts
            .AsNoTracking()
            .Where(c => c.ContractCode.StartsWith(prefix))
            .Select(c => c.ContractCode)
            .ToListAsync(cancellationToken);

        var max = codes
            .Select(code => int.TryParse(code[prefix.Length..], out var n) ? n : 0)
            .DefaultIfEmpty(0)
            .Max();

        return $"{prefix}{max + 1:D4}";
    }

    private async Task<List<RentalContractResponse>> ToResponsesAsync(
        List<RentalContract> contracts,
        Guid currentUserId,
        CancellationToken cancellationToken)
    {
        var ids = contracts.Select(c => c.Id).ToList();
        var companyIds = contracts.Select(c => c.CompanyId).Distinct().ToList();
        var creatorIds = contracts.Select(c => c.CreatedBy).Distinct().ToList();
        var reservationIds = contracts.Where(c => c.ReservationId != null).Select(c => c.ReservationId!.Value).ToList();

        var companies = await db.Companies.AsNoTracking()
            .Where(c => companyIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => new { c.CompanyCode, c.Name }, cancellationToken);

        var creators = await db.Users.AsNoTracking()
            .Where(u => creatorIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.FullName, cancellationToken);

        var reservationCodes = await db.Reservations.AsNoTracking()
            .Where(r => reservationIds.Contains(r.Id))
            .ToDictionaryAsync(r => r.Id, r => r.ReservationCode, cancellationToken);

        var itemStats = await db.RentalContractItems.AsNoTracking()
            .Where(i => ids.Contains(i.RentalContractId))
            .GroupBy(i => i.RentalContractId)
            .Select(g => new { ContractId = g.Key, Count = g.Count(), Total = g.Sum(i => i.Quantity) })
            .ToDictionaryAsync(x => x.ContractId, cancellationToken);

        return contracts.Select(c =>
        {
            var company = companies[c.CompanyId];
            var stats = itemStats.GetValueOrDefault(c.Id);

            return new RentalContractResponse(
                c.Id,
                c.ContractCode,
                c.ReservationId,
                c.ReservationId is null ? null : reservationCodes.GetValueOrDefault(c.ReservationId.Value),
                c.CompanyId,
                company.CompanyCode,
                company.Name,
                c.StartDate,
                c.EndDate,
                c.RentalType,
                EnumName.ToDatabaseValue(c.Status),
                c.ContractTotalAmount,
                c.DepositRequiredAmount,
                c.DepositPaidAmount,
                stats?.Count ?? 0,
                stats?.Total ?? 0,
                c.CreatedBy,
                creators.GetValueOrDefault(c.CreatedBy),
                c.CreatedBy == currentUserId,
                c.RejectionReason,
                c.CreatedAt);
        }).ToList();
    }

    private static RentalContractStatus ParseStatus(string status)
    {
        try
        {
            return EnumName.FromDatabaseValue<RentalContractStatus>(status.Trim().ToUpperInvariant());
        }
        catch (ArgumentException)
        {
            throw new AppException("INVALID_CONTRACT_STATUS", "Trạng thái không hợp lệ.", StatusCodes.Status400BadRequest, "status");
        }
    }

    private async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            throw new AppException(
                "DUPLICATE_CONTRACT",
                "Phiếu đã được tạo hợp đồng hoặc mã hợp đồng bị trùng, vui lòng thử lại.",
                StatusCodes.Status409Conflict);
        }
    }

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
