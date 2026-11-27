using HeThongHoTroChoThueThietBi.Common.Exceptions;
using HeThongHoTroChoThueThietBi.Common.FileStorage.Abstractions;
using HeThongHoTroChoThueThietBi.Common.FileStorage.Dtos;
using HeThongHoTroChoThueThietBi.Common.FileStorage.Extensions;
using HeThongHoTroChoThueThietBi.Common.FileStorage.Models;
using HeThongHoTroChoThueThietBi.Common.Notifications.Abstractions;
using HeThongHoTroChoThueThietBi.Common.Persistence;
using HeThongHoTroChoThueThietBi.Features.Equipment.Enums;
using HeThongHoTroChoThueThietBi.Features.Finance.Entities;
using HeThongHoTroChoThueThietBi.Features.Finance.Enums;
using HeThongHoTroChoThueThietBi.Features.Finance.Services;
using HeThongHoTroChoThueThietBi.Features.Operations.Entities;
using HeThongHoTroChoThueThietBi.Features.Operations.Enums;
using HeThongHoTroChoThueThietBi.Features.Equipment.Services;
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
    ShippingFeeService shippingFeeService,
    CompanyDebtGuardService debtGuard,
    INotificationDispatcher notificationDispatcher)
{
    private const string CodePrefix = "HD";
    private const decimal DefaultDepositRate = 0.2m;
    private const string BusinessLicenseNote = "BUSINESS_LICENSE";
    private const int MinAdvanceBookingDays = 3;
    private const int DepositDeadlineDaysBeforeStart = 2;

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

        if (canViewAll)
        {

            query = query.Where(x =>
                x.contract.Status == RentalContractStatus.PendingApproval ||
                (x.contract.Status != RentalContractStatus.Draft && x.contract.ApprovedBy == currentUserId));
        }
        else
        {
            query = query.Where(x => x.contract.CreatedBy == currentUserId);
        }

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
        var contract = await FindAsync(id, tracking: false, cancellationToken);

        if (canViewAll)
        {
            var canManagerSee =
                contract.Status == RentalContractStatus.PendingApproval ||
                (contract.Status != RentalContractStatus.Draft && contract.ApprovedBy == currentUserId);

            if (!canManagerSee)
                throw NotFound();
        }
        else if (contract.CreatedBy != currentUserId)
        {
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

        await debtGuard.EnsureCompanyHasNoBlockingDebtAsync(reservation.CompanyId, cancellationToken: cancellationToken);

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
        await debtGuard.EnsureCompanyHasNoBlockingDebtAsync(contract.CompanyId, excludeContractId: contract.Id, cancellationToken: cancellationToken);

        contract.RejectionReason = null;
        ChangeStatus(contract, RentalContractStatus.PendingApproval, currentUserId, "Gửi duyệt");

        await SaveChangesAsync(cancellationToken);

        var managerIds = await (
            from ur in db.UserRoles.AsNoTracking()
            join r in db.Roles.AsNoTracking() on ur.RoleId equals r.Id
            where r.Code == "MANAGER"
            select ur.UserId
        ).Distinct().ToListAsync(cancellationToken);

        if (managerIds.Count > 0)
        {
            var salesName = await db.Users.AsNoTracking()
                .Where(u => u.Id == currentUserId)
                .Select(u => u.FullName)
                .FirstOrDefaultAsync(cancellationToken) ?? "Sales";
            var companyName = await db.Companies.AsNoTracking()
                .Where(c => c.Id == contract.CompanyId)
                .Select(c => c.Name)
                .FirstOrDefaultAsync(cancellationToken) ?? "";

            await notificationDispatcher.NotifyUsersAsync(
                managerIds,
                $"Hợp đồng mới chờ duyệt ({contract.ContractCode})",
                $"{salesName} vừa gửi duyệt hợp đồng {contract.ContractCode} cho khách hàng {companyName} (tổng giá trị {contract.ContractTotalAmount:N0} đ).",
                "CONTRACT_PENDING_APPROVAL",
                cancellationToken: cancellationToken);
        }

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

        await notificationDispatcher.NotifyUsersAsync(
            [contract.CreatedBy],
            $"Hợp đồng đã được duyệt ({contract.ContractCode})",
            $"Quản lý đã phê duyệt hợp đồng {contract.ContractCode}. Vui lòng xuất bản PDF cho khách hàng ký và tải bản ký lên hệ thống.",
            "CONTRACT_APPROVED",
            cancellationToken: cancellationToken);

        var approveRepEmail = await db.RentalContractRepresentatives.AsNoTracking()
            .Where(r => r.RentalContractId == contract.Id && r.Email != null)
            .Select(r => r.Email!)
            .FirstOrDefaultAsync(cancellationToken);

        await notificationDispatcher.NotifyCompanyAsync(
            contract.CompanyId,
            $"Hợp đồng {contract.ContractCode} đã được duyệt — Chờ ký",
            $"Hợp đồng thuê thiết bị {contract.ContractCode} (từ {contract.StartDate:dd/MM/yyyy} đến {contract.EndDate:dd/MM/yyyy}) đã được phê duyệt. Quý khách vui lòng xem bản PDF, ký xác nhận và gửi lại cho nhân viên kinh doanh.",
            "CONTRACT_APPROVED",
            extraEmails: approveRepEmail is not null ? [approveRepEmail] : null,
            cancellationToken: cancellationToken);

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

        await notificationDispatcher.NotifyUsersAsync(
            [contract.CreatedBy],
            $"Hợp đồng bị từ chối ({contract.ContractCode})",
            $"Quản lý đã từ chối hợp đồng {contract.ContractCode}. Lý do: {reason}. Vui lòng chỉnh sửa và gửi duyệt lại.",
            "CONTRACT_REJECTED",
            cancellationToken: cancellationToken);

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

        var targetManagers = contract.ApprovedBy.HasValue
            ? [contract.ApprovedBy.Value]
            : await (
                from ur in db.UserRoles.AsNoTracking()
                join r in db.Roles.AsNoTracking() on ur.RoleId equals r.Id
                where r.Code == "MANAGER"
                select ur.UserId
            ).Distinct().ToListAsync(cancellationToken);

        if (targetManagers.Count > 0)
        {
            await notificationDispatcher.NotifyUsersAsync(
                targetManagers,
                $"Bản PDF đã ký chờ duyệt ({contract.ContractCode})",
                $"Nhân viên kinh doanh vừa tải bản hợp đồng PDF đã ký cho {contract.ContractCode}. Vui lòng kiểm tra và duyệt bản ký để mở bước đặt cọc.",
                "CONTRACT_SIGNED_PENDING_REVIEW",
                cancellationToken: cancellationToken);
        }

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

        // Không cho tạo hợp đồng thuê ngay hôm nay: ngày bắt đầu thuê phải cách hôm nay tối thiểu 3 ngày
        // để đảm bảo kịp ký duyệt, đặt cọc (trước ngày thuê 2 ngày), soạn kho và giao hàng.
        var minStartDate = EquipmentAvailabilityService.BusinessToday().AddDays(MinAdvanceBookingDays);
        if (reservation.RequestedFrom < minStartDate)
        {
            throw new AppException(
                "START_DATE_TOO_SOON",
                $"Ngày bắt đầu thuê phải cách hôm nay tối thiểu {MinAdvanceBookingDays} ngày (từ {minStartDate:dd/MM/yyyy} trở đi). Hãy mở lại phiếu giữ chỗ để điều chỉnh ngày thuê trước khi lập hợp đồng.",
                StatusCodes.Status409Conflict);
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

    /// <summary>Số ngày thuê tính cả ngày đầu và ngày cuối.</summary>
    public static int RentalDays(DateOnly start, DateOnly end) => end.DayNumber - start.DayNumber + 1;

    /// <summary>DAILY: số ngày; MONTHLY: số tháng làm tròn lên (30 ngày/tháng).</summary>
    public static int BillableUnits(string billingUnit, int days)
        => billingUnit == "MONTHLY" ? (int)Math.Ceiling(days / 30m) : days;

    /// <summary>
    /// Manager duyệt bản hợp đồng PDF đã ký:
    /// - Nếu hợp đồng yêu cầu đặt cọc và chưa đóng đủ: SIGNED → PENDING_DEPOSIT (mở cổng đặt cọc VNPAY / Sales thu trực tiếp).
    /// - Nếu không yêu cầu cọc (hoặc đã đóng đủ cọc): SIGNED → PREPARING để Kho soạn thiết bị ngay.
    /// </summary>
    public async Task<RentalContractDetailResponse> ConfirmSignedAsync(
        Guid id,
        Guid managerId,
        CancellationToken cancellationToken = default)
    {
        var contract = await FindAsync(id, tracking: true, cancellationToken);
        EnsureAssignedManager(contract, managerId);
        EnsureStatus(contract, RentalContractStatus.Signed);

        var needsDeposit = contract.DepositRequiredAmount > 0 && contract.DepositPaidAmount < contract.DepositRequiredAmount;
        var deadlineDate = contract.StartDate.AddDays(-DepositDeadlineDaysBeforeStart);

        contract.RejectionReason = null;

        if (needsDeposit)
        {
            ChangeStatus(
                contract,
                RentalContractStatus.PendingDeposit,
                managerId,
                $"Quản lý đã duyệt bản PDF đã ký — mở đặt cọc ({contract.DepositRequiredAmount:N0} đ, hạn chót trước ngày {deadlineDate:dd/MM/yyyy}).");
        }
        else
        {
            ChangeStatus(
                contract,
                RentalContractStatus.Preparing,
                managerId,
                "Quản lý đã duyệt bản PDF đã ký (không yêu cầu cọc / đã đủ cọc) — chuyển sang chuẩn bị.");
        }

        await SaveChangesAsync(cancellationToken);

        if (needsDeposit)
        {
            var repEmail = await db.RentalContractRepresentatives.AsNoTracking()
                .Where(r => r.RentalContractId == contract.Id && r.Email != null)
                .Select(r => r.Email!)
                .FirstOrDefaultAsync(cancellationToken);

            var msg = $"Bản ký hợp đồng {contract.ContractCode} đã được Quản lý phê duyệt. " +
                      $"Vui lòng hoàn tất tiền đặt cọc {contract.DepositRequiredAmount:N0} đ trước ngày {deadlineDate:dd/MM/yyyy} " +
                      $"(trước ngày bắt đầu thuê {contract.StartDate:dd/MM/yyyy} 02 ngày). " +
                      $"Quá thời hạn trên mà chưa nhận đủ tiền cọc, hệ thống sẽ tự động hủy hợp đồng.";

            await notificationDispatcher.NotifyCompanyAsync(
                contract.CompanyId,
                $"Hợp đồng {contract.ContractCode} đã duyệt bản ký — Mời đặt cọc",
                msg,
                "CONTRACT_PENDING_DEPOSIT",
                extraEmails: repEmail is not null ? [repEmail] : null,
                cancellationToken: cancellationToken);

            await notificationDispatcher.NotifyUsersAsync(
                [contract.CreatedBy],
                $"Hợp đồng {contract.ContractCode} đã duyệt bản ký — Chờ đặt cọc",
                $"Quản lý đã duyệt bản ký {contract.ContractCode}. Bạn có thể gửi mã QR VNPAY hoặc xác nhận nhận cọc trực tiếp (hạn chót trước {deadlineDate:dd/MM/yyyy}).",
                "CONTRACT_PENDING_DEPOSIT",
                cancellationToken: cancellationToken);
        }

        return await GetDetailAsync(id, managerId, cancellationToken);
    }

    /// <summary>Manager từ chối bản PDF đã ký (SIGNED → PENDING_SIGNATURE) yêu cầu Sales nộp lại.</summary>
    public async Task<RentalContractDetailResponse> RejectSignedAsync(
        Guid id,
        RejectRentalContractRequest request,
        Guid managerId,
        CancellationToken cancellationToken = default)
    {
        var contract = await FindAsync(id, tracking: true, cancellationToken);
        EnsureAssignedManager(contract, managerId);
        EnsureStatus(contract, RentalContractStatus.Signed);

        var reason = request.Reason.Trim();
        contract.RejectionReason = reason;
        ChangeStatus(contract, RentalContractStatus.PendingSignature, managerId, $"Yêu cầu nộp lại bản ký: {reason}");

        await SaveChangesAsync(cancellationToken);

        await notificationDispatcher.NotifyUsersAsync(
            [contract.CreatedBy],
            $"Yêu cầu nộp lại bản PDF đã ký ({contract.ContractCode})",
            $"Quản lý yêu cầu nộp lại bản hợp đồng PDF đã ký cho {contract.ContractCode}. Lý do: {reason}.",
            "CONTRACT_SIGNED_REJECTED",
            cancellationToken: cancellationToken);

        return await GetDetailAsync(id, managerId, cancellationToken);
    }

    /// <summary>Sales gửi yêu cầu hủy từ bước PREPARING trở đi (→ CANCELLATION_REQUESTED).</summary>
    public async Task<RentalContractDetailResponse> RequestCancelAsync(
        Guid id,
        CancelRentalContractRequest request,
        Guid salesId,
        CancellationToken cancellationToken = default)
    {
        var contract = await FindAsync(id, tracking: true, cancellationToken);
        EnsureOwner(contract, salesId);
        EnsureStatus(
            contract,
            RentalContractStatus.PendingDeposit,
            RentalContractStatus.Preparing,
            RentalContractStatus.PartiallyDelivered,
            RentalContractStatus.Delivered);

        var reason = NormalizeOptional(request.Reason);
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new AppException(
                "CANCELLATION_REASON_REQUIRED",
                "Vui lòng nhập lý do yêu cầu hủy hợp đồng.",
                StatusCodes.Status400BadRequest,
                "reason");
        }

        contract.CancellationReason = reason;
        contract.CancellationRequestedAt = DateTimeOffset.UtcNow;
        contract.CancellationRequestedBy = salesId;
        ChangeStatus(contract, RentalContractStatus.CancellationRequested, salesId, $"Yêu cầu hủy: {reason}");

        await SaveChangesAsync(cancellationToken);

        var cancelManagers = contract.ApprovedBy.HasValue
            ? [contract.ApprovedBy.Value]
            : await (
                from ur in db.UserRoles.AsNoTracking()
                join r in db.Roles.AsNoTracking() on ur.RoleId equals r.Id
                where r.Code == "MANAGER"
                select ur.UserId
            ).Distinct().ToListAsync(cancellationToken);

        if (cancelManagers.Count > 0)
        {
            await notificationDispatcher.NotifyUsersAsync(
                cancelManagers,
                $"Yêu cầu hủy hợp đồng chờ duyệt ({contract.ContractCode})",
                $"Nhân viên kinh doanh gửi yêu cầu hủy hợp đồng {contract.ContractCode}. Lý do: {reason}. Vui lòng vào xét duyệt.",
                "CONTRACT_CANCEL_REQUESTED",
                cancellationToken: cancellationToken);
        }

        return await GetDetailAsync(id, salesId, cancellationToken);
    }

    /// <summary>
    /// Manager hủy trực tiếp hoặc duyệt/từ chối yêu cầu hủy từ bước PREPARING trở đi.
    /// - Phiếu chưa xuất kho: hủy phiếu, trả máy về AVAILABLE, hủy/cập nhật chuyến giao.
    /// - Phiếu đang trên đường giao (SHIPPING): hủy chuyến, tự động sinh phiếu thu hồi (PROCESSING)
    ///   để kho kiểm đếm đủ serial và chuyển vào MAINTENANCE + tạo MaintenanceTask kiểm tra bảo dưỡng.
    /// - Tài chính: giữ toàn bộ tiền cọc đã đóng, tạo khoản phí vận chuyển + phạt hủy ngang, cấn trừ vào tiền cọc
    ///   (phần thiếu tạo khoản thu UNPAID) và gửi thông báo App + Email cho công ty.
    /// </summary>
    public async Task<RentalContractDetailResponse> ManagerDecideCancelAsync(
        Guid id,
        bool approve,
        string? reason,
        decimal? penaltyAmount,
        decimal? transportFeeAmount,
        Guid managerId,
        CancellationToken cancellationToken = default)
    {
        var contract = await FindAsync(id, tracking: true, cancellationToken);

        if (contract.ApprovedBy is not null)
            EnsureAssignedManager(contract, managerId);

        if (approve)
        {
            if (contract.Status is RentalContractStatus.Completed or RentalContractStatus.Cancelled)
            {
                throw new AppException(
                    "CONTRACT_STATUS_INVALID",
                    "Hợp đồng đã kết thúc hoặc đã hủy.",
                    StatusCodes.Status409Conflict);
            }

            var hasDeliveredHandover = await db.HandoverNotes
                .AsNoTracking()
                .AnyAsync(h => h.RentalContractId == id && h.Status == HandoverNoteStatus.Completed, cancellationToken);

            if (hasDeliveredHandover)
            {
                throw new AppException(
                    "CONTRACT_ALREADY_DELIVERED",
                    "Hợp đồng đã bàn giao thiết bị cho khách. Vui lòng lập phiếu thu hồi thiết bị trước khi thanh lý/hủy hợp đồng.",
                    StatusCodes.Status409Conflict);
            }

            var now = DateTimeOffset.UtcNow;
            var today = EquipmentAvailabilityService.BusinessToday();
            var note = NormalizeOptional(reason) ?? contract.CancellationReason ?? "Quản lý hủy hợp đồng";

            contract.CancellationReason = note;
            contract.CancelledAt = now;
            contract.CancelledBy = managerId;
            ChangeStatus(contract, RentalContractStatus.Cancelled, managerId, note);

            // 1. Xử lý các phiếu bàn giao chưa hoàn tất của hợp đồng
            var activeHandovers = await db.HandoverNotes
                .Where(h => h.RentalContractId == id && h.Status != HandoverNoteStatus.Cancelled && h.Status != HandoverNoteStatus.Completed)
                .ToListAsync(cancellationToken);

            var affectedAssignmentIds = activeHandovers
                .Where(h => h.ServiceAssignmentId.HasValue)
                .Select(h => h.ServiceAssignmentId!.Value)
                .Distinct()
                .ToList();

            var unshippedNoteIds = activeHandovers
                .Where(h => h.Status is HandoverNoteStatus.Draft or HandoverNoteStatus.Preparing or HandoverNoteStatus.PendingApproval)
                .Select(h => h.Id)
                .ToList();

            if (unshippedNoteIds.Count > 0)
            {
                var reservedEquipIds = await db.HandoverItems
                    .AsNoTracking()
                    .Where(i => unshippedNoteIds.Contains(i.HandoverNoteId))
                    .Select(i => i.EquipmentId)
                    .Distinct()
                    .ToListAsync(cancellationToken);

                if (reservedEquipIds.Count > 0)
                {
                    await db.Equipments
                        .Where(e => reservedEquipIds.Contains(e.Id) && e.Status == EquipmentStatus.Reserved)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(e => e.Status, EquipmentStatus.Available)
                            .SetProperty(e => e.UpdatedAt, now), cancellationToken);
                }
            }

            var shippingHandovers = activeHandovers
                .Where(h => h.Status == HandoverNoteStatus.Shipping)
                .ToList();

            var generatedReturnCodes = new List<string>();
            if (shippingHandovers.Count > 0)
            {
                var returnPrefix = $"TH-{today.Year}-";
                var existingRetCodes = await db.EquipmentReturns
                    .AsNoTracking()
                    .Where(r => r.ReturnCode.StartsWith(returnPrefix))
                    .Select(r => r.ReturnCode)
                    .ToListAsync(cancellationToken);

                var nextRetSeq = existingRetCodes
                    .Select(c => int.TryParse(c[returnPrefix.Length..], out var n) ? n : 0)
                    .DefaultIfEmpty(0)
                    .Max() + 1;

                foreach (var sh in shippingHandovers)
                {
                    var itemsOnTruck = await db.HandoverItems
                        .AsNoTracking()
                        .Where(i => i.HandoverNoteId == sh.Id)
                        .ToListAsync(cancellationToken);

                    if (itemsOnTruck.Count > 0)
                    {
                        var returnCode = $"{returnPrefix}{nextRetSeq++:D4}";
                        generatedReturnCodes.Add(returnCode);

                        var retEntity = new EquipmentReturn
                        {
                            Id = Guid.CreateVersion7(),
                            ReturnCode = returnCode,
                            RentalContractId = contract.Id,
                            ReturnedAt = now,
                            ReceivedBy = managerId,
                            Status = EquipmentReturnStatus.Processing,
                            ReturnType = "CANCELLED_SHIPMENT",
                            Note = $"Thu hồi do hủy giao giữa chừng ({sh.HandoverCode}): {note}. Kho bắt buộc kiểm đủ serial và chuyển bảo dưỡng trước khi nhập kho.",
                            CreatedAt = now,
                            UpdatedAt = now,
                        };
                        db.EquipmentReturns.Add(retEntity);

                        foreach (var item in itemsOnTruck)
                        {
                            db.EquipmentReturnItems.Add(new EquipmentReturnItem
                            {
                                Id = Guid.CreateVersion7(),
                                EquipmentReturnId = retEntity.Id,
                                EquipmentId = item.EquipmentId,
                                Status = EquipmentReturnItemStatus.Returned,
                                Note = "Chờ kho kiểm nhận & chuyển bảo dưỡng",
                            });
                        }
                    }

                    sh.FailedReason = $"Hợp đồng bị hủy giữa chừng lúc đang giao: {note}";
                    sh.FailedAt = now;
                }
            }

            foreach (var h in activeHandovers)
            {
                h.Status = HandoverNoteStatus.Cancelled;
                h.Note = string.IsNullOrWhiteSpace(h.Note)
                    ? $"Hủy theo hợp đồng: {note}"
                    : $"{h.Note}Hủy theo hợp đồng: {note}";
                h.UpdatedAt = now;
            }

            // 2. Cập nhật hoặc hủy chuyến giao (service_assignments) liên quan & báo đội kỹ thuật
            if (affectedAssignmentIds.Count > 0)
            {
                var assignments = await db.ServiceAssignments
                    .Where(a => affectedAssignmentIds.Contains(a.Id)
                                && a.Status != ServiceAssignmentStatus.Completed
                                && a.Status != ServiceAssignmentStatus.Cancelled)
                    .ToListAsync(cancellationToken);

                var cancelledIdsSet = activeHandovers.Select(h => h.Id).ToHashSet();

                foreach (var assignment in assignments)
                {
                    var remainingStops = await db.HandoverNotes
                        .Where(h => h.ServiceAssignmentId == assignment.Id
                                    && h.Status != HandoverNoteStatus.Cancelled
                                    && !cancelledIdsSet.Contains(h.Id))
                        .OrderBy(h => h.StopOrder)
                        .ToListAsync(cancellationToken);

                    var memberUserIds = await (
                        from m in db.ServiceAssignmentMembers.AsNoTracking()
                        join s in db.Staff.AsNoTracking() on m.StaffId equals s.Id
                        where m.ServiceAssignmentId == assignment.Id
                        select s.UserId
                    ).ToListAsync(cancellationToken);

                    if (remainingStops.Count == 0)
                    {
                        assignment.Status = ServiceAssignmentStatus.Cancelled;
                        assignment.Description = string.IsNullOrWhiteSpace(assignment.Description)
                            ? $"Hủy do hợp đồng {contract.ContractCode} bị hủy."
                            : $"{assignment.Description} Hủy do hợp đồng {contract.ContractCode} bị hủy.";
                        assignment.UpdatedAt = now;

                        await notificationDispatcher.NotifyUsersAsync(
                            memberUserIds,
                            $"Hủy chuyến giao: {assignment.Title}",
                            shippingHandovers.Count > 0
                                ? $"Hợp đồng {contract.ContractCode} bị hủy giữa chừng. Vui lòng mang toàn bộ thiết bị về kho để thủ kho kiểm nhận ({string.Join(", ", generatedReturnCodes)})."
                                : $"Hợp đồng {contract.ContractCode} đã bị hủy ({note}). Chuyến giao này đã được hủy.",
                            "DELIVERY_CANCELLED",
                            cancellationToken: cancellationToken);
                    }
                    else
                    {
                        for (var idx = 0; idx < remainingStops.Count; idx++)
                        {
                            remainingStops[idx].StopOrder = idx + 1;
                            remainingStops[idx].UpdatedAt = now;
                        }

                        var remainingCodes = await db.RentalContracts
                            .AsNoTracking()
                            .Where(c => remainingStops.Select(s => s.RentalContractId).Contains(c.Id))
                            .Select(c => c.ContractCode)
                            .ToListAsync(cancellationToken);

                        assignment.Title = $"Giao lắp {remainingStops.Count} điểm ({string.Join(", ", remainingCodes)})";
                        assignment.UpdatedAt = now;

                        await notificationDispatcher.NotifyUsersAsync(
                            memberUserIds,
                            $"Cập nhật tuyến giao: bỏ điểm {contract.ContractCode}",
                            $"Hợp đồng {contract.ContractCode} đã bị hủy. Vui lòng bỏ qua điểm này và tiếp tục giao {remainingStops.Count} điểm còn lại.",
                            "DELIVERY_UPDATED",
                            cancellationToken: cancellationToken);
                    }
                }
            }

            // 3. Tài chính: giữ toàn bộ tiền cọc, tính phí vận chuyển + phạt hủy ngang, cấn trừ vào tiền cọc
            var transportFee = Math.Max(0m, transportFeeAmount ?? 0m);
            var penaltyFee = Math.Max(0m, penaltyAmount ?? 0m);
            var createdCharges = new List<RentalCharge>();

            if (transportFee > 0m)
            {
                var transportCharge = new RentalCharge
                {
                    Id = Guid.CreateVersion7(),
                    SourceType = "CONTRACT",
                    RentalContractId = contract.Id,
                    ChargeType = "TRANSPORT",
                    Description = $"Phí vận chuyển phát sinh do hủy hợp đồng {contract.ContractCode}",
                    Quantity = 1,
                    UnitPrice = transportFee,
                    Amount = transportFee,
                    DueDate = today.AddDays(3),
                    PaidAmount = 0m,
                    Status = RentalChargeStatus.Unpaid,
                    CreatedBy = managerId,
                    CreatedAt = now,
                    Note = note,
                };
                db.RentalCharges.Add(transportCharge);
                createdCharges.Add(transportCharge);
            }

            if (penaltyFee > 0m)
            {
                var penaltyCharge = new RentalCharge
                {
                    Id = Guid.CreateVersion7(),
                    SourceType = "CONTRACT",
                    RentalContractId = contract.Id,
                    ChargeType = "EARLY_CANCELLATION",
                    Description = $"Phí phạt hủy hợp đồng {contract.ContractCode}: {note}",
                    Quantity = 1,
                    UnitPrice = penaltyFee,
                    Amount = penaltyFee,
                    DueDate = today.AddDays(3),
                    PaidAmount = 0m,
                    Status = RentalChargeStatus.Unpaid,
                    CreatedBy = managerId,
                    CreatedAt = now,
                    Note = note,
                };
                db.RentalCharges.Add(penaltyCharge);
                createdCharges.Add(penaltyCharge);
            }

            var totalPenaltyAndShipping = transportFee + penaltyFee;
            var availableDeposit = Math.Max(0m, contract.DepositPaidAmount - contract.DepositAppliedAmount - contract.DepositRefundedAmount);
            var offsetFromDeposit = Math.Min(availableDeposit, totalPenaltyAndShipping);

            if (offsetFromDeposit > 0m)
            {
                contract.DepositAppliedAmount += offsetFromDeposit;
                var remainingOffset = offsetFromDeposit;

                foreach (var charge in createdCharges)
                {
                    if (remainingOffset <= 0m) break;
                    var apply = Math.Min(charge.Amount, remainingOffset);
                    charge.PaidAmount = apply;
                    remainingOffset -= apply;

                    if (charge.PaidAmount >= charge.Amount)
                    {
                        charge.Status = RentalChargeStatus.Paid;
                        charge.PaidAt = now;
                    }
                    else
                    {
                        charge.Status = RentalChargeStatus.PartiallyPaid;
                    }
                }
            }

            var remainingToPay = Math.Max(0m, totalPenaltyAndShipping - offsetFromDeposit);

            var repEmail = await db.RentalContractRepresentatives
                .AsNoTracking()
                .Where(r => r.RentalContractId == contract.Id && r.Email != null)
                .Select(r => r.Email!)
                .FirstOrDefaultAsync(cancellationToken);

            var summaryLines = new List<string>
            {
                $"Hợp đồng {contract.ContractCode} đã được xác nhận hủy.",
                $"Lý do: {note}",
            };

            if (contract.DepositPaidAmount > 0m || totalPenaltyAndShipping > 0m)
            {
                summaryLines.Add($"• Tiền đặt cọc đã đóng (giữ lại theo quy định): {contract.DepositPaidAmount:N0} đ");
                if (transportFee > 0m)
                    summaryLines.Add($"• Phí vận chuyển phát sinh: {transportFee:N0} đ");
                if (penaltyFee > 0m)
                    summaryLines.Add($"• Phí phạt hủy hợp đồng: {penaltyFee:N0} đ");
                if (offsetFromDeposit > 0m)
                    summaryLines.Add($"• Đã cấn trừ từ tiền đặt cọc: {offsetFromDeposit:N0} đ");
                if (remainingToPay > 0m)
                    summaryLines.Add($"• Số tiền còn phải thanh toán bổ sung (hạn {today.AddDays(3):dd/MM/yyyy}): {remainingToPay:N0} đ");
            }

            var notifBody = string.Join("", summaryLines);
            await notificationDispatcher.NotifyCompanyAsync(
                contract.CompanyId,
                $"Thông báo hủy hợp đồng & quyết toán phí ({contract.ContractCode})",
                notifBody,
                "CONTRACT_CANCELLED_PENALTY",
                extraEmails: repEmail is not null ? [repEmail] : null,
                cancellationToken: cancellationToken);

            await notificationDispatcher.NotifyUsersAsync(
                [contract.CreatedBy],
                $"Hợp đồng {contract.ContractCode} đã hủy",
                notifBody,
                "CONTRACT_CANCELLED",
                cancellationToken: cancellationToken);
        }
        else
        {
            EnsureStatus(contract, RentalContractStatus.CancellationRequested);

            var lastReq = await db.ContractStatusHistory
                .AsNoTracking()
                .Where(h => h.RentalContractId == id && h.Status == RentalContractStatus.CancellationRequested)
                .OrderByDescending(h => h.ChangedAt)
                .FirstOrDefaultAsync(cancellationToken);

            var restoreStatus = lastReq?.PreviousStatus ?? RentalContractStatus.Preparing;
            var rejectNote = NormalizeOptional(reason) ?? "Không đồng ý hủy hợp đồng";
            ChangeStatus(contract, restoreStatus, managerId, $"Từ chối yêu cầu hủy: {rejectNote}");
        }

        await SaveChangesAsync(cancellationToken);
        return await GetDetailAsync(id, managerId, cancellationToken);
    }

    private static void EnsureAssignedManager(RentalContract contract, Guid managerUserId)
    {
        if (contract.ApprovedBy != managerUserId)
        {
            throw new AppException(
                "FORBIDDEN_MANAGER",
                "Hợp đồng này do quản lý khác phụ trách.",
                StatusCodes.Status403Forbidden);
        }
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

    // ================================================================ QUYẾT TOÁN CUỐI HỢP ĐỒNG & HOÀN CỌC (CỤM 3)

    /// <summary>Xem trước bảng đối soát quyết toán cuối hợp đồng: máy còn chưa trả, các khoản nợ/phạt, cấn trừ cọc và hoàn cọc.</summary>
    public async Task<ContractSettlementPreviewResponse> GetSettlementPreviewAsync(
        Guid contractId,
        Guid managerId,
        CancellationToken ct = default)
    {
        var contract = await FindAsync(contractId, tracking: false, ct);
        EnsureAssignedManager(contract, managerId);

        var companyName = await db.Companies.AsNoTracking()
            .Where(co => co.Id == contract.CompanyId)
            .Select(co => co.Name)
            .SingleAsync(ct);

        var outstandingCount = await CountOutstandingEquipmentsForContractAsync(contractId, ct);
        var pendingReturnsCount = await db.EquipmentReturns.AsNoTracking()
            .CountAsync(r => r.RentalContractId == contractId
                             && (r.Status == EquipmentReturnStatus.Draft || r.Status == EquipmentReturnStatus.Processing), ct);

        var unpaidItems = await GetUnpaidSettlementItemsAsync(contract, ct);
        var totalUnpaid = unpaidItems.Sum(x => x.RemainingAmount);
        var availableDeposit = Math.Max(0m, contract.DepositPaidAmount - contract.DepositAppliedAmount - contract.DepositRefundedAmount);

        var projectedOffset = Math.Min(availableDeposit, totalUnpaid);
        var projectedRefund = Math.Max(0m, availableDeposit - projectedOffset);
        var projectedShortfall = Math.Max(0m, totalUnpaid - projectedOffset);

        string? blockingReason = null;
        if (contract.Status is RentalContractStatus.Draft or RentalContractStatus.PendingApproval
            or RentalContractStatus.Rejected or RentalContractStatus.PendingSignature or RentalContractStatus.Signed)
        {
            blockingReason = "Hợp đồng chưa được triển khai bàn giao, không thể quyết toán cuối kỳ.";
        }
        else if (outstandingCount > 0)
        {
            blockingReason = $"Vẫn còn {outstandingCount} thiết bị đang ở hiện trường chưa thu hồi về kho.";
        }
        else if (pendingReturnsCount > 0)
        {
            blockingReason = $"Đang có {pendingReturnsCount} phiếu thu hồi chưa hoàn tất kiểm nhận tại kho.";
        }
        else if (projectedShortfall > 0)
        {
            blockingReason = $"Sau khi cấn trừ hết tiền cọc, khách hàng vẫn còn thiếu {projectedShortfall:N0} đ. Có thể cấn trừ cọc trước; hợp đồng sẽ đóng COMPLETED sau khi khách thanh toán dứt điểm phần thiếu.";
        }

        var canComplete = outstandingCount == 0 && pendingReturnsCount == 0 && projectedShortfall == 0m
                          && contract.Status is not (RentalContractStatus.Completed or RentalContractStatus.Cancelled);

        return new ContractSettlementPreviewResponse(
            contract.Id,
            contract.ContractCode,
            companyName,
            EnumName.ToDatabaseValue(contract.Status),
            contract.StartDate,
            contract.EndDate,
            outstandingCount,
            pendingReturnsCount,
            contract.DepositRequiredAmount,
            contract.DepositPaidAmount,
            contract.DepositAppliedAmount,
            contract.DepositRefundedAmount,
            availableDeposit,
            totalUnpaid,
            projectedOffset,
            projectedRefund,
            projectedShortfall,
            canComplete,
            blockingReason,
            unpaidItems);
    }

    /// <summary>Manager xác nhận quyết toán cuối hợp đồng: cấn trừ nợ/phạt vào cọc, ghi nhận hoàn cọc dư và chốt COMPLETED nếu đủ điều kiện.</summary>
    public async Task<SettleContractResponse> SettleContractAsync(
        Guid contractId,
        SettleContractRequest request,
        Guid managerId,
        CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var contract = await db.RentalContracts
            .FromSqlInterpolated($"SELECT * FROM rental_contracts WHERE id = {contractId} FOR UPDATE")
            .SingleOrDefaultAsync(ct)
            ?? throw NotFound();

        EnsureAssignedManager(contract, managerId);

        if (contract.Status == RentalContractStatus.Completed)
        {
            throw new AppException("ALREADY_COMPLETED", "Hợp đồng này đã được quyết toán hoàn tất trước đó.", StatusCodes.Status409Conflict);
        }

        if (contract.Status is RentalContractStatus.Draft or RentalContractStatus.PendingApproval
            or RentalContractStatus.Rejected or RentalContractStatus.PendingSignature or RentalContractStatus.Signed)
        {
            throw new AppException("INVALID_STATUS", "Hợp đồng chưa đi vào vận hành, không thể quyết toán cuối kỳ.", StatusCodes.Status409Conflict);
        }

        var outstandingCount = await CountOutstandingEquipmentsForContractAsync(contractId, ct);
        var pendingReturnsCount = await db.EquipmentReturns.AsNoTracking()
            .CountAsync(r => r.RentalContractId == contractId
                             && (r.Status == EquipmentReturnStatus.Draft || r.Status == EquipmentReturnStatus.Processing), ct);

        if (outstandingCount > 0 || pendingReturnsCount > 0)
        {
            throw new AppException(
                "EQUIPMENTS_NOT_FULLY_RETURNED",
                $"Chưa thể quyết toán cuối kỳ: còn {outstandingCount} thiết bị chưa thu hồi và {pendingReturnsCount} phiếu thu hồi đang chờ xử lý tại kho.",
                StatusCodes.Status409Conflict);
        }

        var now = DateTimeOffset.UtcNow;
        var availableDeposit = Math.Max(0m, contract.DepositPaidAmount - contract.DepositAppliedAmount - contract.DepositRefundedAmount);
        var newlyOffset = 0m;

        if (request.ApplyDepositToUnpaidCharges && availableDeposit > 0)
        {
            var openCharges = await db.RentalCharges
                .Where(rc => rc.RentalContractId == contract.Id
                             && (rc.Status == RentalChargeStatus.Unpaid || rc.Status == RentalChargeStatus.PartiallyPaid)
                             && rc.Amount > rc.PaidAmount)
                .OrderBy(rc => rc.ChargeType == "RENTAL" ? 1 : 0)
                .ThenBy(rc => rc.DueDate ?? DateOnly.MaxValue)
                .ThenBy(rc => rc.CreatedAt)
                .ToListAsync(ct);

            foreach (var ch in openCharges)
            {
                if (availableDeposit <= 0) break;
                var need = ch.Amount - ch.PaidAmount;
                var apply = Math.Min(need, availableDeposit);
                if (apply <= 0) continue;

                ch.PaidAmount += apply;
                availableDeposit -= apply;
                newlyOffset += apply;

                if (ch.PaidAmount >= ch.Amount)
                {
                    ch.Status = RentalChargeStatus.Paid;
                    ch.PaidAt = now;
                }
                else
                {
                    ch.Status = RentalChargeStatus.PartiallyPaid;
                }

                var offsetNote = $"Quyết toán cấn trừ {apply:N0} đ từ tiền đặt cọc.";
                ch.Note = string.IsNullOrWhiteSpace(ch.Note) ? offsetNote : $"{ch.Note} | {offsetNote}";

                if (ch.Status == RentalChargeStatus.Paid)
                {
                    var linkedIcs = await db.IncidentCharges
                        .Where(ic => ic.RentalChargeId == ch.Id && ic.Status == IncidentChargeStatus.Pending)
                        .ToListAsync(ct);
                    foreach (var ic in linkedIcs)
                        ic.Status = IncidentChargeStatus.Charged;
                }
            }

            if (availableDeposit > 0)
            {
                var eqIdsOfContract = await (
                    from hn in db.HandoverNotes.AsNoTracking()
                    join hi in db.HandoverItems.AsNoTracking() on hn.Id equals hi.HandoverNoteId
                    where hn.RentalContractId == contract.Id
                    select hi.EquipmentId
                ).Distinct().ToListAsync(ct);

                var standaloneIcs = await (
                    from ic in db.IncidentCharges
                    join ii in db.EquipmentIncidentItems.AsNoTracking() on ic.IncidentItemId equals ii.Id
                    join inc in db.EquipmentIncidents.AsNoTracking() on ii.IncidentId equals inc.Id
                    where ic.Status == IncidentChargeStatus.Pending
                          && ic.RentalChargeId == null
                          && inc.CompanyId == contract.CompanyId
                          && eqIdsOfContract.Contains(ii.EquipmentId)
                    orderby ic.CreatedAt
                    select ic
                ).ToListAsync(ct);

                foreach (var ic in standaloneIcs)
                {
                    var need = Math.Max(0m, ic.Amount - ic.DepositOffsetAmount);
                    if (need <= 0 || availableDeposit < need) continue;

                    ic.DepositOffsetAmount += need;
                    ic.Status = IncidentChargeStatus.Charged;
                    ic.SettlementMethod = "OFFSET_DEPOSIT";
                    availableDeposit -= need;
                    newlyOffset += need;
                }
            }

            if (newlyOffset > 0)
            {
                contract.DepositAppliedAmount += newlyOffset;
            }
        }

        await db.SaveChangesAsync(ct);
        var remainingUnpaidItems = await GetUnpaidSettlementItemsAsync(contract, ct);
        var remainingDebt = remainingUnpaidItems.Sum(x => x.RemainingAmount);

        var newlyRefunded = 0m;
        if (request.RefundRemainingDeposit && remainingDebt == 0m && availableDeposit > 0m)
        {
            var desiredRefund = request.CustomRefundAmount ?? availableDeposit;
            if (desiredRefund > availableDeposit)
            {
                throw new AppException(
                    "REFUND_EXCEEDS_AVAILABLE_DEPOSIT",
                    $"Số tiền hoàn cọc ({desiredRefund:N0} đ) vượt quá số dư cọc khả dụng ({availableDeposit:N0} đ).",
                    StatusCodes.Status400BadRequest);
            }

            newlyRefunded = desiredRefund;
            contract.DepositRefundedAmount += newlyRefunded;
            availableDeposit -= newlyRefunded;
        }

        var isCompleted = remainingDebt == 0m && contract.Status != RentalContractStatus.Cancelled;
        var summaryParts = new List<string>();
        if (newlyOffset > 0) summaryParts.Add($"cấn trừ cọc {newlyOffset:N0} đ");
        if (newlyRefunded > 0) summaryParts.Add($"hoàn cọc {newlyRefunded:N0} đ");
        if (remainingDebt > 0) summaryParts.Add($"khách còn thiếu {remainingDebt:N0} đ");
        if (!string.IsNullOrWhiteSpace(request.Note)) summaryParts.Add($"Ghi chú: {request.Note.Trim()}");

        var historyNote = summaryParts.Count > 0
            ? $"Quyết toán hợp đồng: {string.Join(", ", summaryParts)}."
            : "Quyết toán hoàn tất hợp đồng.";

        if (isCompleted)
        {
            ChangeStatus(contract, RentalContractStatus.Completed, managerId, historyNote);
        }
        else
        {
            contract.UpdatedAt = now;
            AddHistory(contract, contract.Status, contract.Status, managerId, historyNote);
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        var repEmail = await db.RentalContractRepresentatives.AsNoTracking()
            .Where(r => r.RentalContractId == contract.Id && r.Email != null)
            .Select(r => r.Email!)
            .FirstOrDefaultAsync(ct);

        var customerMsg = isCompleted
            ? $"Hợp đồng {contract.ContractCode} đã được quyết toán hoàn tất (COMPLETED). {historyNote}"
            : $"Biên bản quyết toán hợp đồng {contract.ContractCode}: {historyNote} Vui lòng thanh toán nốt phần công nợ còn lại ({remainingDebt:N0} đ) để đóng hợp đồng và tiếp tục tạo đơn mới.";

        await notificationDispatcher.NotifyCompanyAsync(
            contract.CompanyId,
            isCompleted
                ? $"Quyết toán hoàn tất hợp đồng {contract.ContractCode}"
                : $"Thông báo quyết toán & công nợ còn thiếu ({contract.ContractCode})",
            customerMsg,
            "CONTRACT_SETTLED",
            extraEmails: repEmail is not null ? [repEmail] : null,
            cancellationToken: ct);

        await notificationDispatcher.NotifyUsersAsync(
            [contract.CreatedBy],
            $"Quyết toán hợp đồng {contract.ContractCode}",
            customerMsg,
            "CONTRACT_SETTLED",
            cancellationToken: ct);

        var resultMsg = isCompleted
            ? $"Đã quyết toán hoàn tất hợp đồng {contract.ContractCode} (cấn trừ cọc {newlyOffset:N0} đ, hoàn cọc {newlyRefunded:N0} đ)."
            : $"Đã ghi nhận cấn trừ cọc {newlyOffset:N0} đ. Khách hàng còn thiếu {remainingDebt:N0} đ — hợp đồng sẽ đóng COMPLETED sau khi khách thanh toán đủ.";

        return new SettleContractResponse(
            contract.Id,
            contract.ContractCode,
            EnumName.ToDatabaseValue(contract.Status),
            newlyOffset,
            newlyRefunded,
            availableDeposit,
            remainingDebt,
            isCompleted,
            resultMsg);
    }

    private async Task<int> CountOutstandingEquipmentsForContractAsync(Guid contractId, CancellationToken ct)
    {
        return await (
            from note in db.HandoverNotes.AsNoTracking()
            join item in db.HandoverItems.AsNoTracking() on note.Id equals item.HandoverNoteId
            join eq in db.Equipments.AsNoTracking() on item.EquipmentId equals eq.Id
            where note.RentalContractId == contractId
                  && note.Status == HandoverNoteStatus.Completed
                  && eq.Status == EquipmentStatus.Rented
                  && !(from ret in db.EquipmentReturns
                       join ri in db.EquipmentReturnItems on ret.Id equals ri.EquipmentReturnId
                       where ret.RentalContractId == contractId
                             && ret.Status != EquipmentReturnStatus.Cancelled
                             && ri.EquipmentId == eq.Id
                       select ri.Id).Any()
            select eq.Id
        ).Distinct().CountAsync(ct);
    }

    private async Task<List<SettlementChargeItemDto>> GetUnpaidSettlementItemsAsync(
        RentalContract contract,
        CancellationToken ct)
    {
        var rentalCharges = await db.RentalCharges.AsNoTracking()
            .Where(rc => rc.RentalContractId == contract.Id
                         && (rc.Status == RentalChargeStatus.Unpaid || rc.Status == RentalChargeStatus.PartiallyPaid)
                         && rc.Amount > rc.PaidAmount)
            .OrderBy(rc => rc.ChargeType == "RENTAL" ? 1 : 0)
            .ThenBy(rc => rc.DueDate ?? DateOnly.MaxValue)
            .ToListAsync(ct);

        var result = rentalCharges.Select(rc => new SettlementChargeItemDto(
            rc.Id,
            "RENTAL_CHARGE",
            rc.ChargeType,
            rc.Description ?? rc.ChargeType,
            rc.Amount,
            rc.PaidAmount,
            Math.Max(0m, rc.Amount - rc.PaidAmount),
            EnumName.ToDatabaseValue(rc.Status),
            rc.DueDate)).ToList();

        var linkedRcIds = rentalCharges.Select(r => r.Id).ToHashSet();
        var eqIdsOfContract = await (
            from hn in db.HandoverNotes.AsNoTracking()
            join hi in db.HandoverItems.AsNoTracking() on hn.Id equals hi.HandoverNoteId
            where hn.RentalContractId == contract.Id
            select hi.EquipmentId
        ).Distinct().ToListAsync(ct);

        if (eqIdsOfContract.Count > 0)
        {
            var standaloneIcs = await (
                from ic in db.IncidentCharges.AsNoTracking()
                join ii in db.EquipmentIncidentItems.AsNoTracking() on ic.IncidentItemId equals ii.Id
                join inc in db.EquipmentIncidents.AsNoTracking() on ii.IncidentId equals inc.Id
                where ic.Status == IncidentChargeStatus.Pending
                      && (ic.RentalChargeId == null || !linkedRcIds.Contains(ic.RentalChargeId.Value))
                      && inc.CompanyId == contract.CompanyId
                      && eqIdsOfContract.Contains(ii.EquipmentId)
                select new { ic, inc.IncidentCode }
            ).ToListAsync(ct);

            foreach (var row in standaloneIcs)
            {
                var rem = Math.Max(0m, row.ic.Amount - row.ic.DepositOffsetAmount);
                if (rem <= 0) continue;
                result.Add(new SettlementChargeItemDto(
                    row.ic.Id,
                    "INCIDENT_CHARGE",
                    row.ic.ChargeType,
                    row.ic.Description ?? $"Phí sự cố {row.IncidentCode}",
                    row.ic.Amount,
                    row.ic.DepositOffsetAmount,
                    rem,
                    EnumName.ToDatabaseValue(row.ic.Status),
                    null));
            }
        }

        return result;
    }

}
