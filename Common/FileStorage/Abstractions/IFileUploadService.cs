using HeThongHoTroChoThueThietBi.Common.FileStorage.Dtos;
using HeThongHoTroChoThueThietBi.Common.FileStorage.Models;

namespace HeThongHoTroChoThueThietBi.Common.FileStorage.Abstractions;

public interface IFileUploadService
{
    Task<FileUploadTicket> CreateUploadTicketAsync(FileUploadPolicy policy, FileUploadRequest request, Guid uploadedBy, CancellationToken cancellationToken = default);
    Task<ConfirmFileUploadResponse> ConfirmUploadAsync(ConfirmFileUploadRequest request, Guid uploadedBy, CancellationToken cancellationToken = default);
}