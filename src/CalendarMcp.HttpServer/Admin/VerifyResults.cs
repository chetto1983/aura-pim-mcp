using CalendarMcp.Core.Providers;

namespace CalendarMcp.HttpServer.Admin;

/// <summary>
/// The answers a check gives, the same on create, on «Prova di nuovo» and on a partial change. A failure names the
/// step and the reason and nothing else: what the server said goes to the log only (spec amendment A8).
/// </summary>
internal static class VerifyResults
{
    public static IResult Failed(VerifyFailure f) =>
        Results.Json(new { error = "verify_failed", step = f.Step, reason = f.Reason }, statusCode: StatusCodes.Status422UnprocessableEntity);

    public static IResult TooMany() =>
        Results.Json(new { error = "too_many_attempts" }, statusCode: StatusCodes.Status429TooManyRequests);

    /// <summary>A 400 naming what is wrong with the request, in a word a client can switch on.</summary>
    public static IResult Refused(string error) => Results.BadRequest(new { error });
}
