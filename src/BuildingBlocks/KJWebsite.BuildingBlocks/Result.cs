namespace KJWebsite.BuildingBlocks;

public sealed record ResultError(string Code, string Message);

public sealed record Result(bool IsSuccess, ResultError? Error)
{
    public static Result Success() => new(true, null);

    public static Result Failure(string code, string message) => new(false, new ResultError(code, message));

    public static Result<T> Success<T>(T value) => new(true, value, null);

    public static Result<T> Failure<T>(string code, string message) => new(false, default, new ResultError(code, message));
}

public sealed record Result<T>(bool IsSuccess, T? Value, ResultError? Error);
