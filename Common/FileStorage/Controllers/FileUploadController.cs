using HeThongHoTroChoThueThietBi.Common.FileStorage.Abstractions;
using HeThongHoTroChoThueThietBi.Common.FileStorage.Dtos;
using HeThongHoTroChoThueThietBi.Common.FileStorage.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

[ApiController]
[Route("api/file-upload")]
public class FileUploadController : ControllerBase
{
    private readonly IFileUploadService _uploadService;

    public FileUploadController(IFileUploadService uploadService)
    {
        _uploadService = uploadService;
    }

    [HttpPost("confirm")]
    public async Task<IActionResult> Confirm([FromBody] ConfirmFileUploadRequest request, CancellationToken cancellationToken)
    {
        var userId = GetUserId();

        var result = await _uploadService.ConfirmUploadAsync(request, userId, cancellationToken);

        return Ok(result);
    }

    [Authorize]
    private Guid GetUserId()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (!Guid.TryParse(userId, out var id))
            throw new UnauthorizedAccessException("Không xác định được người dùng.");

        return id;
    }

}