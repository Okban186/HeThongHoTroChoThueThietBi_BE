namespace HeThongHoTroChoThueThietBi.Common.Exceptions;

public class AppException : Exception
{
    public string ErrorCode { get; }

    public int StatusCode { get; }

    public string? Field { get; }

    public AppException(
        string errorCode,
        string message,
        int statusCode = 400,
        string? field = null
    ) : base(message)
    {
        ErrorCode = errorCode;
        StatusCode = statusCode;
        Field = field;
    }
}