using Microsoft.AspNetCore.Diagnostics;
using SyWater.Notifications.Domain.Common;
using SyWater.Notifications.Domain.Messages;
using SyWater.Notifications.Domain.Notifications;
using SyWater.Notifications.Domain.Preferences;
using SyWater.Notifications.Domain.Push;
using SyWater.Notifications.Domain.ShortMessages;
using SyWater.Notifications.Application.Ports.Out;

namespace SyWater.Notifications.Api.Errors;

/// <summary>Domain exceptions → RFC 9457 Problem Details with the stable code in "title" (same as the other services).</summary>
public sealed class DomainExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        var (status, code) = exception switch
        {
            NotificationNotFoundException e => (StatusCodes.Status404NotFound, e.Code),
            InvalidNotificationException e => (StatusCodes.Status400BadRequest, e.Code),
            InvalidAudienceException e => (StatusCodes.Status400BadRequest, e.Code),
            CriticalChannelRequiredException e => (StatusCodes.Status400BadRequest, e.Code),
            SmsOnlyForCriticalException e => (StatusCodes.Status400BadRequest, e.Code),
            InvalidPushTokenException e => (StatusCodes.Status400BadRequest, e.Code),
            InvalidPushPlatformException e => (StatusCodes.Status400BadRequest, e.Code),
            InvalidShortMessageException e => (StatusCodes.Status400BadRequest, e.Code),
            ShortMessageRejectedException => (StatusCodes.Status422UnprocessableEntity, "sms.rejected"),
            ShortMessageSendException => (StatusCodes.Status503ServiceUnavailable, "sms.unavailable"),
            ShortMessagesDisabledException => (StatusCodes.Status503ServiceUnavailable, "sms.disabled"),
            InvalidMessageTemplateException e => (StatusCodes.Status400BadRequest, e.Code),
            MessageTemplateNotFoundException e => (StatusCodes.Status404NotFound, e.Code),
            ExternalServiceUnavailableException => (StatusCodes.Status503ServiceUnavailable, "service.unavailable"),
            UnauthorizedAccessException => (StatusCodes.Status401Unauthorized, "auth.invalid_token"),
            _ => (0, ""),
        };

        if (status == 0) return false; // unknown error: ASP.NET Core answers a generic 500

        context.Response.StatusCode = status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            Exception = exception,
            ProblemDetails =
            {
                Status = status,
                Title = code,
                Detail = exception.Message,
                Type = exception is DomainException ? $"https://sywater.dev/errors/{code}" : null,
            },
        });
    }
}
