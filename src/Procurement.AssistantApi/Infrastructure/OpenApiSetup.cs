using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Procurement.AssistantApi.Model;

namespace Procurement.AssistantApi.Infrastructure;

public static class OpenApiSetup
{
    public const string AgentGuidance = """
        API for an AI procurement assistant acting on behalf of a signed-in user.

        Rules for agents:
        1. Raising a request never commits spend: it is created as PendingApproval and a person approves it.
        2. Look up codes with the /v1/reference endpoints; never invent cost centers, plants or materials. Prices come from the catalogue.
        3. Call validateRequisitionRequest first and fix every issue before creating.
        4. Send a new Idempotency-Key (UUID) per user intent on create, and reuse it only when retrying that same request.
        5. Branch on problem.code, not on titles. If problem.retryable is false, do not retry; follow problem.suggestedAction.
        6. Relay statusReason and nextExpectedStep to the user as written.
        7. Withdrawing needs the user's explicit confirmation and the ETag from the latest read.
        """;

    public static IServiceCollection AddAssistantOpenApi(this IServiceCollection services) =>
        services.AddOpenApi(options =>
        {
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info = new OpenApiInfo
                {
                    Title = "Procurement Assistant API",
                    Version = "1.0.0",
                    Description = AgentGuidance,
                };
                document.Components ??= new OpenApiComponents();
                document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
                document.Components.SecuritySchemes["bearer"] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.Http,
                    Scheme = "bearer",
                    BearerFormat = "JWT",
                    Description = "User-delegated token. Scopes: requisitions:read, requisitions:draft.",
                };
                return Task.CompletedTask;
            });

            // The server rejects unknown fields; say so in the contract so agents see the same rule.
            options.AddSchemaTransformer((schema, context, _) =>
            {
                if (context.JsonTypeInfo.Type == typeof(CreateRequisitionRequest) || context.JsonTypeInfo.Type == typeof(RequestLine))
                {
                    schema.AdditionalPropertiesAllowed = false;
                }
                return Task.CompletedTask;
            });

            options.AddOperationTransformer((operation, context, _) =>
            {
                var hints = context.Description.ActionDescriptor.EndpointMetadata.OfType<AgentToolHints>().FirstOrDefault();
                if (hints is null) return Task.CompletedTask;

                operation.OperationId = hints.ToolName;
                operation.Extensions ??= new Dictionary<string, IOpenApiExtension>();
                operation.Extensions["x-mcp"] = new JsonNodeExtension(new JsonObject
                {
                    ["toolName"] = hints.ToolName,
                    ["readOnlyHint"] = hints.ReadOnly,
                    ["idempotentHint"] = hints.Idempotent,
                    ["destructiveHint"] = hints.Destructive,
                    ["openWorldHint"] = false,
                    ["requiresUserConfirmation"] = hints.Destructive,
                });
                operation.Extensions["x-required-scopes"] = new JsonNodeExtension(new JsonArray(hints.Scopes.Select(s => (JsonNode)s).ToArray()));
                operation.Security ??= [];
                operation.Security.Add(new OpenApiSecurityRequirement
                {
                    [new OpenApiSecuritySchemeReference("bearer", context.Document)] = [.. hints.Scopes],
                });
                return Task.CompletedTask;
            });
        });
}
