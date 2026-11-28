namespace HeThongHoTroChoThueThietBi.Features.Dashboard.DTOs;

public sealed record ManagerDashboardResponse(
    int PendingApprovalContracts,
    int PendingSignedContracts,
    int ReadyToScheduleContracts,
    int PendingCancelContracts,
    int OpenIncidentsCount,
    int PendingReplacementsCount,
    int PendingHandoversCount,
    int PendingDisposalsCount,
    int TodaySchedulesCount,
    int PendingSupplementaryCount = 0,
    int WaitingDefectiveReturnCount = 0,
    int FailedDeliveriesCount = 0,
    int ProcessingReturnsCount = 0,
    int PendingCashPaymentsCount = 0,
    int PendingDepositContracts = 0,
    int PendingIncidentChargesCount = 0,
    int PendingRetrievalContractsCount = 0);
