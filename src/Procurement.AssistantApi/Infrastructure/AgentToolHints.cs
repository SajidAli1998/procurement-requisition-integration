namespace Procurement.AssistantApi.Infrastructure;

/// <summary>
/// Endpoint metadata describing how an AI agent may use the operation. Emitted into OpenAPI as "x-mcp"
/// (mirroring MCP tool annotations) so a tool generator can decide what needs user confirmation.
/// </summary>
public sealed record AgentToolHints(string ToolName, bool ReadOnly, bool Idempotent, bool Destructive, params string[] Scopes);
