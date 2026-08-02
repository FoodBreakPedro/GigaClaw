namespace GigaClaw.Core.Tests.Api;

/// <summary>
/// Serializes the API host test classes relative to each other. Each of them boots the real
/// Program.cs host through a <c>WebApplicationFactory</c> whose class fixture sets the
/// process-global <c>GIGACLAW_DATA_DIR</c> env var and deletes its temp data dir on dispose. The
/// host reads the var lazily on first client creation, so two of these classes running in parallel
/// can start a host against another class's (soon-deleted) data dir — or, after a concurrent
/// Dispose nulls the var, fall back to the user's real %APPDATA%/GigaClaw. Sharing one collection
/// keeps them sequential while the rest of the suite runs in parallel.
/// </summary>
[CollectionDefinition("ApiHost")]
public sealed class ApiHostCollection { }
