using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using SmartBank.Core.Common;

namespace SmartBank.API.Controllers
{
    /// <summary>
    /// What every controller needs: who is calling, and one way to turn a <see cref="ServiceResult{T}"/> into an HTTP answer.
    /// The error body is always <c>{ "isSuccess": false, "errorKey": "...", "message": "..." }</c>; the status code comes from
    /// <see cref="StatusFor"/>, so "not found", "forbidden", "conflict" and "failed" are told apart for monitoring while the
    /// web app keeps reading the same body.
    /// </summary>
    public abstract class SmartBankControllerBase : ControllerBase, IActionFilter
    {
        /// <summary>A signed-in caller whose token carries no usable user id gets 401 before any action runs.</summary>
        [NonAction]
        public virtual void OnActionExecuting(ActionExecutingContext context)
        {
            if (User.Identity?.IsAuthenticated == true && GetUserId() == Guid.Empty)
            {
                context.Result = Unauthorized();
            }
        }

        [NonAction]
        public virtual void OnActionExecuted(ActionExecutedContext context)
        {
        }

        /// <summary>The signed-in user's id from the token, or <see cref="Guid.Empty"/> when there is none.</summary>
        protected Guid GetUserId()
        {
            var value = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            return Guid.TryParse(value, out var userId) ? userId : Guid.Empty;
        }

        /// <summary>The caller's address as the server sees it (the real client behind the proxy once forwarded headers are on).</summary>
        protected string? ClientIp() => HttpContext.Connection.RemoteIpAddress?.ToString();

        /// <summary>200 with the result's data (or <paramref name="successBody"/> of it), or the error answer.</summary>
        protected IActionResult ToActionResult<T>(ServiceResult<T> result, Func<T?, object?>? successBody = null)
        {
            if (!result.IsSuccess)
            {
                return ErrorResult(result.ErrorKey, result.Message);
            }

            return Ok(successBody != null ? successBody(result.Data) : result.Data);
        }

        protected IActionResult ErrorResult(string? errorKey, string? message) =>
            StatusCode(StatusFor(errorKey), new { isSuccess = false, errorKey, message });

        /// <summary>
        /// The HTTP status for a service error key.
        /// 404: the thing the request names does not exist (or is not the caller's: the two look the same).
        /// 403: an explicit refusal. 409: the data changed under the request, or the object is closed. 500: our own failure.
        /// Everything else (validation, business rules, one-time-code challenges) stays 400, which is what the web app expects.
        /// </summary>
        public static int StatusFor(string? errorKey) => errorKey switch
        {
            "AccountNotFound" or "SourceAccountNotFound" or "DestinationAccountNotFound" or "TargetAccountNotFound" or
            "CreditCardNotFound" or "ContactNotFound" or "OrderNotFound" or "SessionNotFound" or "UserNotFound" => StatusCodes.Status404NotFound,

            "UnauthorizedSessionAccess" or "UnauthorizedAccountAccess" => StatusCodes.Status403Forbidden,

            "ConcurrentModification" or "SessionClosed" => StatusCodes.Status409Conflict,

            "TransactionFailed" or "PaymentFailed" or "ChargeFailed" or "AccountCloseFailed" => StatusCodes.Status500InternalServerError,

            _ => StatusCodes.Status400BadRequest
        };
    }
}
