namespace Procurement.AssistantApi.Infrastructure;

// Emitted into OpenAPI as "x-mcp" (MCP tool annotations) so tool generators know what needs user confirmation.
public sealed record AgentToolHints(string ToolName, bool ReadOnly, bool Idempotent, bool Destructive, params string[] Scopes);
