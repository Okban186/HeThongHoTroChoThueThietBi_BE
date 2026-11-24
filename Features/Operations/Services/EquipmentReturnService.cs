using HeThongHoTroChoThueThietBi.Common.Exceptions;
using HeThongHoTroChoThueThietBi.Common.Persistence;
using HeThongHoTroChoThueThietBi.Features.Equipment.Enums;
using HeThongHoTroChoThueThietBi.Features.Equipment.Services;
using HeThongHoTroChoThueThietBi.Features.Operations.DTOs;
using HeThongHoTroChoThueThietBi.Features.Operations.Entities;
using HeThongHoTroChoThueThietBi.Features.Operations.Enums;
using HeThongHoTroChoThueThietBi.Features.Rental.Entities;
using HeThongHoTroChoThueThietBi.Features.Rental.Enums;
using HeThongHoTroChoThueThietBi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HeThongHoTroChoThueThietBi.Features.Operations.Services;

/// <summary>
/// Thủ kho nhận lại máy sau khi kỹ thuật viên thu hồi về kho.
///
/// Quy tắc:
/// - Máy "đang ở chỗ khách" = máy trong phiếu bàn giao đã giao xong (COMPLETED) của hợp đồng,
///   hiện đang RENTED và chưa có trong phiếu nhận trả nào.
/// - Mỗi máy kiểm một trong hai: RETURNED kèm tình trạng (GOOD/FAIR → máy về AVAILABLE;
///   DAMAGED → máy DAMAGED chờ sửa) hoặc MISSING (máy LOST). Phiếu lập xong là COMPLETED.
/// - Hợp đồng đã giao đủ (DELIVERED) và không còn máy ở chỗ khách → COMPLETED.
/// - Tiền bồi thường hư hỏng/mất do quản lý tính sau (rental_charges), ngoài phạm vi thủ kho.
/// - Mã phiếu TH-YYYY-NNNN.
/// </summary>
public sealed class EquipmentReturnService(AppDbContext db)
{
    private const string CodePrefix = "TH";
    private static readonly string[] ReturnConditions = ["GOOD", "FAIR", "DAMAGED"];

    // Hợp đồng có thể còn máy ở chỗ khách.
    private static readonly RentalContractStatus[] ReturnableStatuses =
    [
        RentalContractStatus.PartiallyDelivered,
        RentalContractStatus.Delivered,
        RentalContractStatus.Expired,
        RentalContractStatus.CancellationRequested,
    ];

    // ================================================================ đọc

    public async Task<List<ReturnContractResponse>> GetContractsAsync(
        string? keyword,
        CancellationToken cancellationToken = default)
    {
        var query =
            from contract in db.RentalContracts.AsNoTracking()
            join company in db.Companies.AsNoTracking() on contract.CompanyId equals company.Id
            where ReturnableStatuses.Contains(contract.Status)
            select new { contract, company };

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var pattern = $"%{keyword.Trim()}%";
            query = query.Where(x =>
                EF.Functions.ILike(x.contract.ContractCode, pattern) ||
                EF.Functions.ILike(x.company.Name, pattern) ||
                EF.Functions.ILike(x.company.CompanyCode, pattern));
        }

        var rows = await query
            .OrderBy(x => x.contract.EndDate)
            .ToListAsync(cancellationToken);

        var ids = rows.Select(x => x.contract.Id).ToList();
        var delivered = await DeliveredCountsAsync(ids, cancellationToken);
        var outstanding = (await OutstandingQuery(ids).ToListAsync(cancellationToken))
            .GroupBy(x => x.ContractId)
            .ToDictionary(g => g.Key, g => g.Count());

        // Chỉ hiện hợp đồng còn máy ở chỗ khách.
        return rows
            .Where(x => outstanding.GetValueOrDefault(x.contract.Id) > 0)
            .Select(x => ToContractResponse(x.contract, x.company.CompanyCode, x.company.Name,
                delivered.GetValueOrDefault(x.contract.Id), outstanding.GetValueOrDefault(x.contract.Id)))
            .ToList();
    }

    public async Task<ReturnContractDetailResponse> GetContractDetailAsync(
        Guid contractId,
        CancellationToken cancellationToken = default)
    {
        var row = await (
            from contract in db.RentalContracts.AsNoTracking()
            join company in db.Companies.AsNoTracking() on contract.CompanyId equals company.Id
            where contract.Id == contractId
            select new { contract, company }
        ).SingleOrDefaultAsync(cancellationToken)
            ?? throw new AppException("CONTRACT_NOT_FOUND", "Không tìm thấy hợp đồng.", StatusCodes.Status404NotFound);

        var outstanding = await OutstandingQuery([contractId])
            .OrderBy(x => x.ModelName)
            .ThenBy(x => x.SerialNumber)
            .ToListAsync(cancellationToken);
        var delivered = await DeliveredCountsAsync([contractId], cancellationToken);

        var returns = await ReturnQuery()
            .Where(x => x.ret.RentalContractId == contractId)
            .OrderByDescending(x => x.ret.CreatedAt)
            .ToListAsync(cancellationToken);

        return new ReturnContractDetailResponse(
            ToContractResponse(row.contract, row.company.CompanyCode, row.company.Name,
                delivered.GetValueOrDefault(contractId), outstanding.Count),
            outstanding.Select(x => new OutstandingEquipmentResponse(
                x.EquipmentId, x.SerialNumber, x.EquipmentModelId, x.ModelCode, x.ModelName,
                x.Condition, x.HandoverCode)).ToList(),
            await ToResponsesAsync(returns, cancellationToken));
    }

    public async Task<List<EquipmentReturnResponse>> GetReturnsAsync(
        string? keyword,
        CancellationToken cancellationToken = default)
    {
        var query = ReturnQuery();

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var pattern = $"%{keyword.Trim()}%";
            query = query.Where(x =>
                EF.Functions.ILike(x.ret.ReturnCode, pattern) ||
                EF.Functions.ILike(x.ContractCode, pattern) ||
                EF.Functions.ILike(x.CompanyName, pattern));
        }

        var rows = await query.OrderByDescending(x => x.ret.CreatedAt).ToListAsync(cancellationToken);
        return await ToResponsesAsync(rows, cancellationToken);
    }

    public async Task<EquipmentReturnDetailResponse> GetReturnDetailAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var row = await ReturnQuery().SingleOrDefaultAsync(x => x.ret.Id == id, cancellationToken)
                  ?? throw new AppException("RETURN_NOT_FOUND", "Không tìm thấy phiếu nhận trả.", StatusCodes.Status404NotFound);

        var items = await (
            from item in db.EquipmentReturnItems.AsNoTracking()
            join equipment in db.Equipments.AsNoTracking() on item.EquipmentId equals equipment.Id
            join model in db.EquipmentModels.AsNoTracking() on equipment.EquipmentModelId equals model.Id
            where item.EquipmentReturnId == id
            orderby model.Name, equipment.SerialNumber
            select new { item, equipment, model.Code, model.Name }
        ).ToListAsync(cancellationToken);

        return new EquipmentReturnDetailResponse(
            (await ToResponsesAsync([row], cancellationToken)).Single(),
            items.Select(x => new EquipmentReturnItemResponse(
                x.item.Id,
                x.equipment.Id,
                x.equipment.SerialNumber,
                x.Code,
                x.Name,
                EnumName.ToDatabaseValue(x.item.Status),
                x.equipment.Condition,
                EnumName.ToDatabaseValue(x.equipment.Status),
                x.item.Note)).ToList());
    }

    // ================================================================ ghi

    public async Task<EquipmentReturnDetailResponse> CreateAsync(
        CreateEquipmentReturnRequest request,
        Guid currentUserId,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var contractId = request.RentalContractId!.Value;

        // Khóa hợp đồng để hai phiếu nhận trả cùng hợp đồng không nhận trùng máy.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM rental_contracts WHERE id = {contractId} FOR UPDATE", cancellationToken);

        var contract = await db.RentalContracts.SingleOrDefaultAsync(c => c.Id == contractId, cancellationToken)
                       ?? throw new AppException("CONTRACT_NOT_FOUND", "Không tìm thấy hợp đồng.", StatusCodes.Status404NotFound);

        var requestedIds = request.Items.Select(i => i.EquipmentId!.Value).ToList();

        if (requestedIds.Distinct().Count() != requestedIds.Count)
        {
            throw new AppException("DUPLICATE_EQUIPMENT", "Một máy chỉ được kiểm một lần.",
                StatusCodes.Status400BadRequest, "items");
        }

        var outstandingIds = (await OutstandingQuery([contractId]).Select(x => x.EquipmentId).ToListAsync(cancellationToken))
            .ToHashSet();

        if (requestedIds.Any(id => !outstandingIds.Contains(id)))
        {
            throw new AppException(
                "EQUIPMENT_NOT_OUTSTANDING",
                "Có máy không thuộc hợp đồng này hoặc đã được nhận lại trước đó.",
                StatusCodes.Status400BadRequest,
                "items");
        }

        var now = DateTimeOffset.UtcNow;
        var returnedAt = request.ReturnedAt ?? now;

        if (returnedAt > now.AddMinutes(5))
        {
            throw new AppException("INVALID_RETURNED_AT", "Thời điểm nhận không được ở tương lai.",
                StatusCodes.Status400BadRequest, "returnedAt");
        }

        var equipments = await db.Equipments
            .Where(e => requestedIds.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, cancellationToken);

        var equipmentReturn = new EquipmentReturn
        {
            Id = Guid.CreateVersion7(),
            ReturnCode = await GenerateCodeAsync(cancellationToken),
            RentalContractId = contractId,
            ReturnedAt = returnedAt,
            ReceivedBy = currentUserId,
            Status = EquipmentReturnStatus.Completed,
            Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim(),
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.EquipmentReturns.Add(equipmentReturn);

        foreach (var line in request.Items)
        {
            var equipment = equipments[line.EquipmentId!.Value];
            var status = line.Status.Trim().ToUpperInvariant() switch
            {
                "RETURNED" => EquipmentReturnItemStatus.Returned,
                "MISSING" => EquipmentReturnItemStatus.Missing,
                _ => throw new AppException("INVALID_RETURN_ITEM_STATUS",
                    "Kết quả kiểm máy phải là RETURNED hoặc MISSING.", StatusCodes.Status400BadRequest, "items.status"),
            };

            if (status == EquipmentReturnItemStatus.Returned)
            {
                var condition = line.Condition?.Trim().ToUpperInvariant();

                if (condition is null || !ReturnConditions.Contains(condition))
                {
                    throw new AppException("INVALID_CONDITION",
                        $"Máy {equipment.SerialNumber}: tình trạng phải là GOOD, FAIR hoặc DAMAGED.",
                        StatusCodes.Status400BadRequest, "items.condition");
                }

                equipment.Condition = condition;
                // Máy hỏng chờ kỹ thuật kiểm tra/sửa, chưa cho thuê lại.
                equipment.Status = condition == "DAMAGED" ? EquipmentStatus.Damaged : EquipmentStatus.Available;
            }
            else
            {
                equipment.Status = EquipmentStatus.Lost;
            }

            equipment.UpdatedAt = now;

            db.EquipmentReturnItems.Add(new EquipmentReturnItem
            {
                Id = Guid.CreateVersion7(),
                EquipmentReturnId = equipmentReturn.Id,
                EquipmentId = equipment.Id,
                Status = status,
                Note = string.IsNullOrWhiteSpace(line.Note) ? null : line.Note.Trim(),
            });
        }

        // Đã giao đủ và nhận lại hết → hoàn tất hợp đồng.
        var remaining = outstandingIds.Count - requestedIds.Count;

        if (remaining == 0 && contract.Status is RentalContractStatus.Delivered or RentalContractStatus.Expired)
        {
            var previous = contract.Status;
            contract.Status = RentalContractStatus.Completed;
            contract.UpdatedAt = now;

            db.ContractStatusHistory.Add(new ContractStatusHistory
            {
                Id = Guid.CreateVersion7(),
                RentalContractId = contract.Id,
                Status = RentalContractStatus.Completed,
                PreviousStatus = previous,
                ChangedBy = currentUserId,
                ChangedAt = now,
                Note = $"Kho đã nhận lại toàn bộ thiết bị ({equipmentReturn.ReturnCode})",
            });
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return await GetReturnDetailAsync(equipmentReturn.Id, cancellationToken);
    }

    // ================================================================ hỗ trợ

    private sealed class OutstandingRow
    {
        public Guid ContractId { get; init; }
        public Guid EquipmentId { get; init; }
        public string SerialNumber { get; init; } = null!;
        public Guid EquipmentModelId { get; init; }
        public string ModelCode { get; init; } = null!;
        public string ModelName { get; init; } = null!;
        public string Condition { get; init; } = null!;
        public string HandoverCode { get; init; } = null!;
    }

    /// <summary>Máy đã giao xong của các hợp đồng, đang RENTED và chưa nằm trong phiếu nhận trả nào.</summary>
    private IQueryable<OutstandingRow> OutstandingQuery(List<Guid> contractIds) =>
        from note in db.HandoverNotes.AsNoTracking()
        join item in db.HandoverItems.AsNoTracking() on note.Id equals item.HandoverNoteId
        join equipment in db.Equipments.AsNoTracking() on item.EquipmentId equals equipment.Id
        join model in db.EquipmentModels.AsNoTracking() on equipment.EquipmentModelId equals model.Id
        where contractIds.Contains(note.RentalContractId) &&
              note.Status == HandoverNoteStatus.Completed &&
              equipment.Status == EquipmentStatus.Rented &&
              !(from ret in db.EquipmentReturns
                join retItem in db.EquipmentReturnItems on ret.Id equals retItem.EquipmentReturnId
                where ret.RentalContractId == note.RentalContractId &&
                      ret.Status != EquipmentReturnStatus.Cancelled &&
                      retItem.EquipmentId == equipment.Id
                select retItem.Id).Any()
        select new OutstandingRow
        {
            ContractId = note.RentalContractId,
            EquipmentId = equipment.Id,
            SerialNumber = equipment.SerialNumber,
            EquipmentModelId = model.Id,
            ModelCode = model.Code,
            ModelName = model.Name,
            Condition = equipment.Condition,
            HandoverCode = note.HandoverCode,
        };

    private async Task<Dictionary<Guid, int>> DeliveredCountsAsync(List<Guid> contractIds, CancellationToken cancellationToken) =>
        await (
            from note in db.HandoverNotes.AsNoTracking()
            join item in db.HandoverItems.AsNoTracking() on note.Id equals item.HandoverNoteId
            where contractIds.Contains(note.RentalContractId) && note.Status == HandoverNoteStatus.Completed
            group item by note.RentalContractId into g
            select new { g.Key, Count = g.Count() }
        ).ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);

    private sealed class ReturnRow
    {
        public EquipmentReturn ret { get; init; } = null!;
        public string ContractCode { get; init; } = null!;
        public string CompanyName { get; init; } = null!;
    }

    private IQueryable<ReturnRow> ReturnQuery() =>
        from ret in db.EquipmentReturns.AsNoTracking()
        join contract in db.RentalContracts.AsNoTracking() on ret.RentalContractId equals contract.Id
        join company in db.Companies.AsNoTracking() on contract.CompanyId equals company.Id
        select new ReturnRow { ret = ret, ContractCode = contract.ContractCode, CompanyName = company.Name };

    private async Task<List<EquipmentReturnResponse>> ToResponsesAsync(
        List<ReturnRow> rows,
        CancellationToken cancellationToken)
    {
        var ids = rows.Select(r => r.ret.Id).ToList();
        var receiverIds = rows.Select(r => r.ret.ReceivedBy).Distinct().ToList();

        var stats = await (
            from item in db.EquipmentReturnItems.AsNoTracking()
            join equipment in db.Equipments.AsNoTracking() on item.EquipmentId equals equipment.Id
            where ids.Contains(item.EquipmentReturnId)
            group new { item.Status, equipment.Condition } by item.EquipmentReturnId into g
            select new
            {
                g.Key,
                Returned = g.Count(x => x.Status == EquipmentReturnItemStatus.Returned),
                Missing = g.Count(x => x.Status == EquipmentReturnItemStatus.Missing),
                Damaged = g.Count(x => x.Status == EquipmentReturnItemStatus.Returned && x.Condition == "DAMAGED"),
            }
        ).ToDictionaryAsync(x => x.Key, cancellationToken);

        var receivers = await db.Users.AsNoTracking()
            .Where(u => receiverIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.FullName, cancellationToken);

        return rows.Select(r =>
        {
            var s = stats.GetValueOrDefault(r.ret.Id);

            return new EquipmentReturnResponse(
                r.ret.Id,
                r.ret.ReturnCode,
                r.ret.RentalContractId,
                r.ContractCode,
                r.CompanyName,
                r.ret.ReturnedAt,
                receivers.GetValueOrDefault(r.ret.ReceivedBy),
                EnumName.ToDatabaseValue(r.ret.Status),
                r.ret.Note,
                s?.Returned ?? 0,
                s?.Missing ?? 0,
                s?.Damaged ?? 0,
                r.ret.CreatedAt);
        }).ToList();
    }

    private static ReturnContractResponse ToContractResponse(
        RentalContract contract, string companyCode, string companyName, int delivered, int outstanding) =>
        new(
            contract.Id,
            contract.ContractCode,
            companyCode,
            companyName,
            contract.StartDate,
            contract.EndDate,
            EnumName.ToDatabaseValue(contract.Status),
            contract.DeliveryAddress,
            delivered,
            outstanding);

    private async Task<string> GenerateCodeAsync(CancellationToken cancellationToken)
    {
        var prefix = $"{CodePrefix}-{EquipmentAvailabilityService.BusinessToday().Year}-";

        var codes = await db.EquipmentReturns
            .AsNoTracking()
            .Where(r => r.ReturnCode.StartsWith(prefix))
            .Select(r => r.ReturnCode)
            .ToListAsync(cancellationToken);

        var max = codes
            .Select(code => int.TryParse(code[prefix.Length..], out var n) ? n : 0)
            .DefaultIfEmpty(0)
            .Max();

        return $"{prefix}{max + 1:D4}";
    }
}
