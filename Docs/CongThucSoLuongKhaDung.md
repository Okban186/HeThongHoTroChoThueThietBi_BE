# Công thức số lượng thiết bị khả dụng

> Áp dụng cho mọi nghiệp vụ cần biết "model X còn bao nhiêu máy trong khoảng ngày [từ, đến]":
> đặt thuê, hợp đồng, kho, thay thế thiết bị...
> **Không tự viết lại công thức**, hãy gọi `EquipmentAvailabilityService`.

## 1. Công thức

```
Khả dụng[từ, đến] = Số máy hoạt động − Mức chiếm dụng cao nhất trong 1 ngày của [từ, đến]
                    (tối thiểu 0)
```

### Số máy hoạt động
Số bản ghi `equipments` của model có `status` thuộc:

| Tính | Không tính |
|---|---|
| `AVAILABLE`, `RESERVED`, `RENTED` | `MAINTENANCE`, `DAMAGED`, `LOST`, `DISPOSED` |

Máy đang cho thuê (`RENTED`) vẫn được tính vì phần chiếm dụng của nó đã được trừ qua hợp đồng.

### Chiếm dụng
| Nguồn | Điều kiện | Khoảng ngày chiếm |
|---|---|---|
| Phiếu giữ chỗ (`reservation_items`) | Phiếu `PENDING` hoặc `APPROVED` **và** `expires_at > hiện tại` | `requested_from` → `requested_to` |
| Hợp đồng (`rental_contract_items`) | Mọi trạng thái **trừ** `COMPLETED`, `EXPIRED`, `CANCELLED` | `start_date` → `end_date` |
| Hợp đồng đã giao quá hạn | `DELIVERED` / `PARTIALLY_DELIVERED` mà `end_date` < hôm nay | `start_date` → **hôm nay** (máy vẫn ở chỗ khách) |

- Phiếu hết hạn tự nhả máy nhờ điều kiện `expires_at`, **không cần job nền** để tính đúng.
- Phiếu đã `CONVERTED` không tính nữa, vì số lượng đã chuyển sang hợp đồng.
- Ngày tính **trọn ngày** (kết thúc ngày 3 và bắt đầu ngày 3 là trùng nhau).
- Ngày nghiệp vụ tính theo giờ Việt Nam (UTC+7).

### Vì sao lấy "mức cao nhất trong 1 ngày"?
Ví dụ có 5 máy, hỏi khả dụng 01/12 → 10/12:
- Phiếu A giữ 2 máy 01/12 → 03/12, phiếu B giữ 3 máy 05/12 → 08/12 (không trùng nhau)
- Ngày cao nhất chỉ chiếm 3 máy, nên **khả dụng = 5 − 3 = 2**. Nếu cộng dồn sẽ ra 0, sai.

## 2. Cách dùng trong Backend

```csharp
public sealed class ReservationService(EquipmentAvailabilityService availability)
{
    // Một model
    var result = await availability.GetAvailabilityAsync(
        equipmentModelId, from, to,
        excludeReservationId: reservation.Id, // khi đang sửa phiếu này
        cancellationToken: ct);

    if (requestedQuantity > result.AvailableQuantity)
        throw new AppException("INSUFFICIENT_EQUIPMENT", "...", 409);

    // Nhiều model cùng lúc (1 lần truy vấn)
    var results = await availability.GetAvailabilityAsync(
        modelIds, from, to, cancellationToken: ct);
}
```

- Khi **sửa** phiếu hoặc hợp đồng, truyền `excludeReservationId` / `excludeRentalContractId` để không tự trừ số lượng của chính nó.
- Khi tạo hợp đồng từ phiếu, truyền `excludeReservationId` = phiếu nguồn, vì phiếu sẽ chuyển sang `CONVERTED`.
- Gộp số lượng theo model trước khi kiểm tra, để không lách giới hạn bằng cách tách nhiều dòng cùng model.

## 3. API cho Flutter

Quyền: `ADMIN, MANAGER, SALES, STOREKEEPER, TECHNICIAN`.

| Method | Đường dẫn | Mô tả |
|---|---|---|
| GET | `/api/equipment-models?keyword=` | Model đang hoạt động, kèm `operationalUnits`, `availableToday` |
| GET | `/api/equipment-models/{id}` | Chi tiết model |
| GET | `/api/equipment-models/{id}/availability?from=2026-12-01&to=2026-12-10&excludeReservationId=` | Khả dụng 1 model |
| POST | `/api/equipment-models/availability` | Khả dụng nhiều model |

Body của POST:
```json
{
  "equipmentModelIds": ["..."],
  "from": "2026-12-01",
  "to": "2026-12-10",
  "excludeReservationId": null
}
```

Kết quả:
```json
{
  "equipmentModelId": "...",
  "from": "2026-12-01",
  "to": "2026-12-10",
  "operationalUnits": 5,
  "peakHeldQuantity": 3,
  "availableQuantity": 2
}
```

## 4. Giới hạn đã biết
- Kiểm tra rồi mới lưu không chặn tuyệt đối trường hợp hai người cùng giữ chỗ **đúng cùng lúc**. Khi viết API tạo/sửa phiếu hoặc hợp đồng, nên kiểm tra lại số lượng **trong cùng transaction** lúc lưu.
- Hợp đồng đã trả một phần máy vẫn bị tính chiếm đủ số lượng đến khi hợp đồng kết thúc (an toàn, không cho thuê quá số máy).
