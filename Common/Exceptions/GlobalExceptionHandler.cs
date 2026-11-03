using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;

namespace HeThongHoTroChoThueThietBi.Common.Exceptions;

public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        var statusCode = (exception is AppException appException) ? appException.StatusCode : StatusCodes.Status500InternalServerError;

        if (statusCode >= 500)
        {
            logger.LogError(
                exception,
                "An unexpected error occurred."
            );
        }
        else if (exception is AppException appException1)
        {


            logger.LogWarning(
                "Application error: {ErrorCode} - {Message}", appException1.ErrorCode, exception.Message
            );
        }

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";

        var response = new
        {
            success = false,

            code = exception is AppException app
                ? app.ErrorCode
                : "INTERNAL_SERVER_ERROR",

            message = exception is AppException known &&
                      statusCode < 500
                ? known.Message
                : "Đã xảy ra lỗi hệ thống. Vui lòng thử lại sau.",

            errors = exception is AppException fieldError &&
                     fieldError.Field is not null &&
                     statusCode < 500
                ? new Dictionary<string, string[]>
                {
                    [fieldError.Field] = [fieldError.Message]
                }
                : null
        };

        await context.Response.WriteAsync(
            JsonSerializer.Serialize(response),
            cancellationToken
        );

        return true;
    }
}