using System.Collections.Generic;

namespace SmartBank.Core.Common
{
    public class ServiceResult<T>
    {
        public bool IsSuccess { get; set; }
        public T? Data { get; set; }
        public string? ErrorKey { get; set; } // E.g., "UserAlreadyExists", "InvalidCredentials" for frontend i18n
        public string? Message { get; set; }  // Default English fallback message

        public static ServiceResult<T> Success(T data) => new() { IsSuccess = true, Data = data };
        public static ServiceResult<T> Failure(string errorKey, string message) => new() { IsSuccess = false, ErrorKey = errorKey, Message = message };
    }
}
