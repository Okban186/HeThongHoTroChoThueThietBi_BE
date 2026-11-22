namespace HeThongHoTroChoThueThietBi.Features.Rental.DTOs;

public sealed record ReservationDetailResponse(
    ReservationResponse Reservation,
    List<ReservationItemResponse> Items
);
