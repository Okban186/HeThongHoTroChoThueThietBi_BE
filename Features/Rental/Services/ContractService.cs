using HeThongHoTroChoThueThietBi.Common.Exceptions;
using HeThongHoTroChoThueThietBi.Common.FileStorage.Abstractions;
using HeThongHoTroChoThueThietBi.Common.FileStorage.Dtos;
using HeThongHoTroChoThueThietBi.Common.FileStorage.Extensions;
using HeThongHoTroChoThueThietBi.Common.FileStorage.Models;
using HeThongHoTroChoThueThietBi.Common.Persistence;
using HeThongHoTroChoThueThietBi.Features.Equipment.Services;
using HeThongHoTroChoThueThietBi.Features.Rental.DTOs;
using HeThongHoTroChoThueThietBi.Features.Rental.Entities;
using HeThongHoTroChoThueThietBi.Features.Rental.Enums;
using HeThongHoTroChoThueThietBi.Infrastructure.Persistence;
using HeThongHoTroChoThueThietBi.Storage.Entities;
using Microsoft.EntityFrameworkCore;
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
///   Sales tải bản đã ký        → SIGNED
///   Sales hủy (trước khi ký)   → CANCELLED
///
/// Quy tắc:
/// - Thời gian thuê, model, số lượng lấy theo phiếu; Sales chọn loại thuê, đơn vị tính, đơn giá, cọc.
/// - Tiền dòng = số lượng × đơn giá × số đơn vị (ngày, hoặc tháng = ceil(ngày / 30)).
///   Tổng = Σ dòng thiết bị + Σ dịch vụ do bên cho thuê thực hiện.
/// - Giấy tờ bắt buộc: CCCD mặt trước, mặt sau, giấy phép kinh doanh; giấy ủy quyền nếu người ký
///   là người được ủy quyền. File phải do chính Sales upload và chưa dùng cho hợp đồng khác.
///   (Schema chỉ có ID_CARD/AUTHORIZATION_LETTER/OTHER: mặt trước/sau phân biệt bằng display_order,
///   giấy phép kinh doanh là OTHER kèm ghi chú.)
/// - Chỉ người tạo được sửa/gửi duyệt/tải bản ký/hủy; chỉ Manager duyệt/từ chối.
/// - Mỗi lần đổi trạng thái ghi contract_status_history.
/// </summary>
public sealed class ContractService(
    AppDbContext db,
    EquipmentAvailabilityService availabilityService,
    IFileUploadService uploadService)
{
    private const string CodePrefix = "HD";
    private const int LongTermThresholdDays = 30;
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

    private static readonly string[] RentalTypes = ["SHORT_TERM", "LONG_TERM"];
    private static readonly string[] BillingUnits = ["DAILY", "MONTHLY"];
    private static readonly string[] ServiceTypes = ["DELIVERY", "LOADING", "UNLOADING", "OTHER"];
    private static readonly string[] ServiceProviders = ["RENTAL_COMPANY", "CUSTOMER"];
    private static readonly string[] RepresentativeTypes = ["LEGAL_REPRESENTATIVE", "AUTHORIZED_REPRESENTATIVE"];

    // ================================================================ đọc

    public async Task<List<RentalContractResponse>> GetContractsAsync(
        string? keyword,
        string? status,
        bool onlyMine,
        Guid currentUserId,
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

        if (onlyMine)
            query = query.Where(x => x.contract.CreatedBy == currentUserId);

        var contracts = await query
            .OrderByDescending(x => x.contract.CreatedAt)
            .Select(x => x.contract)
            .ToListAsync(cancellationToken);

        return await ToResponsesAsync(contracts, currentUserId, cancellationToken);
    }

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
        var longTerm = days >= LongTermThresholdDays;
        var billingUnit = longTerm ? "MONTHLY" : "DAILY";

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
            longTerm ? x.model.RentalPriceMonthly : x.model.RentalPriceDaily,
            days,
            x.item.Note)).ToList();

        var total = lines.Sum(l => l.Amount);

        return new RentalContractSuggestionResponse(
            reservation.Id,
            reservation.ReservationCode,
            company.Id,
            company.Name,
            reservation.RequestedFrom,
            reservation.RequestedTo,
            days,
            longTerm ? "LONG_TERM" : "SHORT_TERM",
            DefaultDepositRate,
            lines,
            total,
            RoundToThousand(total * DefaultDepositRate));
    }

    // ================================================================ upload giấy tờ

    public Task<FileUploadTicket> CreateUploadTicketAsync(
        FileUploadRequest request,
        Guid currentUserId,
        CancellationToken cancellationToken = default)
        => uploadService.CreateUploadTicketAsync(DocumentUploadPolicy, request, currentUserId, cancellationToken);

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

    /// <summary>Sales tải bản hợp đồng đã ký (PENDING_SIGNATURE → SIGNED).</summary>
    public async Task<RentalContractDetailResponse> MarkSignedAsync(
        Guid id,
        MarkRentalContractSignedRequest request,
        Guid currentUserId,
        CancellationToken cancellationToken = default)
    {
        var contract = await FindAsync(id, tracking: true, cancellationToken);
        EnsureOwner(contract, currentUserId);
        EnsureStatus(contract, RentalContractStatus.PendingSignature);

        await EnsureFilesUsableAsync([request.ContractFileId!.Value], currentUserId, contract.Id, cancellationToken);

        var now = DateTimeOffset.UtcNow;
        contract.ContractFileId = request.ContractFileId;
        contract.SignedAt = now;
        contract.EffectiveFrom = contract.StartDate;
        contract.EffectiveTo = contract.EndDate;
        ChangeStatus(contract, RentalContractStatus.Signed, currentUserId, "Đã ký hợp đồng");

        await SaveChangesAsync(cancellationToken);
        return await GetDetailAsync(id, currentUserId, cancellationToken);
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
        var rentalType = RequireOneOf(content.RentalType, RentalTypes, "rentalType", "Loại thuê không hợp lệ.");
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

        await EnsureFilesUsableAsync(fileIds, currentUserId, contract.Id, cancellationToken);

        var days = RentalDays(contract.StartDate, contract.EndDate);

        // --- dòng thiết bị
        if (!isNew)
        {
            db.RentalContractItems.RemoveRange(
                await db.RentalContractItems.Where(i => i.RentalContractId == contract.Id).ToListAsync(cancellationToken));
        }

        decimal itemsTotal = 0;

        foreach (var source in sourceItems)
        {
            var line = requestedItems[source.EquipmentModelId];
            var billingUnit = RequireOneOf(line.BillingUnit, BillingUnits, "items.billingUnit", "Đơn vị tính không hợp lệ.");

            itemsTotal += source.Quantity * line.AgreedUnitPrice * BillableUnits(billingUnit, days);

            db.RentalContractItems.Add(new RentalContractItem
            {
                Id = Guid.CreateVersion7(),
                RentalContractId = contract.Id,
                EquipmentModelId = source.EquipmentModelId,
                Quantity = source.Quantity,
                BillingUnit = billingUnit,
                AgreedUnitPrice = line.AgreedUnitPrice,
                Note = NormalizeOptional(line.Note),
            });
        }

        // --- dịch vụ
        if (!isNew)
        {
            db.RentalContractServices.RemoveRange(
                await db.RentalContractServices.Where(s => s.RentalContractId == contract.Id).ToListAsync(cancellationToken));
        }

        decimal servicesTotal = 0;

        foreach (var service in content.Services)
        {
            var providedBy = RequireOneOf(service.ProvidedBy, ServiceProviders, "services.providedBy", "Bên thực hiện không hợp lệ.");
            var amount = service.Quantity * service.UnitPrice;

            if (providedBy == "RENTAL_COMPANY")
                servicesTotal += amount;

            db.RentalContractServices.Add(new ContractServiceEntity
            {
                Id = Guid.CreateVersion7(),
                RentalContractId = contract.Id,
                ServiceType = RequireOneOf(service.ServiceType, ServiceTypes, "services.serviceType", "Loại dịch vụ không hợp lệ."),
                ProvidedBy = providedBy,
                Quantity = service.Quantity,
                UnitPrice = service.UnitPrice,
                Amount = amount,
                Note = NormalizeOptional(service.Note),
            });
        }

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
        contract.RentalType = rentalType;
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
    private async Task EnsureFilesUsableAsync(
        List<Guid> fileIds,
        Guid currentUserId,
        Guid contractId,
        CancellationToken cancellationToken)
    {
        var files = await db.StorageFiles
            .AsNoTracking()
            .Where(f => fileIds.Contains(f.Id))
            .Select(f => new { f.Id, f.UploadedBy })
            .ToListAsync(cancellationToken);

        if (files.Count != fileIds.Distinct().Count() || files.Any(f => f.UploadedBy != currentUserId))
        {
            throw new AppException(
                "DOCUMENT_FILE_INVALID",
                "Ảnh giấy tờ không hợp lệ hoặc không phải do bạn tải lên.",
                StatusCodes.Status400BadRequest,
                "documents");
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
                "Ảnh giấy tờ đã được dùng cho hợp đồng khác.",
                StatusCodes.Status409Conflict,
                "documents");
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

        return reservation;
    }

    private async Task<RentalContract> FindAsync(Guid id, bool tracking, CancellationToken cancellationToken)
    {
        var query = tracking ? db.RentalContracts : db.RentalContracts.AsNoTracking();

        return await query.SingleOrDefaultAsync(c => c.Id == id, cancellationToken)
               ?? throw new AppException("CONTRACT_NOT_FOUND", "Không tìm thấy hợp đồng.", StatusCodes.Status404NotFound);
    }

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
