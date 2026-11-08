namespace SharpDbg.DapProtocol;

/// <summary>
/// An error returned from a request handler, or received in response to a request.
/// Sent to the peer as an <c>ErrorResponse</c>.
/// </summary>
/// <param name="Message">The error message. Sent as both the response's short <c>message</c> and the <c>error.format</c> shown to users.</param>
public sealed record DapError(string Message)
{
	/// <summary>Optional unique identifier for this kind of error.</summary>
	public int? Id { get; init; }

	/// <summary>If true, the client should show the error to the user.</summary>
	public bool ShowUser { get; init; }

	/// <summary>The error returned when a request is cancelled.</summary>
	public static DapError Cancelled { get; } = new("cancelled");
}

/// <summary>
/// The outcome of a request: either the response body <typeparamref name="T"/>, or a <see cref="DapError"/>.
/// (Hand-written stand-in for upstream's C# 15 <c>union DapResult&lt;T&gt;(T, DapError)</c>, whose runtime
/// types <c>System.Runtime.CompilerServices.IUnion</c>/<c>UnionAttribute</c> only exist in .NET 11. The
/// desktop adapter targets net10.0 so OpenDevelop's .NET 10 SDK host can launch it.)
/// </summary>
public readonly struct DapResult<T> where T : notnull
{
	/// <summary>The response body, when the request succeeded.</summary>
	public T? Body { get; }

	/// <summary>The error, when the request failed.</summary>
	public DapError? Error { get; }

	/// <summary>The body or the error, as an object, matching the union's generated <c>Value</c>.</summary>
	public object? Value => Error is { } error ? error : Body;

	private DapResult(T? body, DapError? error)
	{
		Body = body;
		Error = error;
	}

	public static implicit operator DapResult<T>(T body) => new(body, null);

	public static implicit operator DapResult<T>(DapError error) => new(default, error);
}
