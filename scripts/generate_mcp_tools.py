#!/usr/bin/env python3
"""Derives MCP tool definitions from the assistant API's OpenAPI document.

Each operation with an "x-mcp" extension becomes one tool: its inputSchema merges path/query
parameters and the JSON body, with $refs inlined; its annotations are the MCP tool hints.
Usage: python3 scripts/generate_mcp_tools.py api/assistant-api.openapi.json > api/mcp-tools.json
"""
import json
import sys


def resolve(node, components):
    if isinstance(node, dict):
        if "$ref" in node:
            name = node["$ref"].split("/")[-1]
            return resolve(components["schemas"][name], components)
        return {k: resolve(v, components) for k, v in node.items()}
    if isinstance(node, list):
        return [resolve(v, components) for v in node]
    return node


def main(path):
    with open(path, encoding="utf-8") as f:
        doc = json.load(f)
    components = doc.get("components", {})
    tools = []
    for route, operations in doc["paths"].items():
        for method, op in operations.items():
            hints = op.get("x-mcp")
            if not hints:
                continue
            properties, required = {}, []
            for p in op.get("parameters", []):
                schema = resolve(p.get("schema", {}), components)
                if p.get("description"):
                    schema["description"] = p["description"]
                properties[p["name"]] = schema
                if p.get("required"):
                    required.append(p["name"])
            body = op.get("requestBody", {}).get("content", {}).get("application/json", {}).get("schema")
            if body:
                body = resolve(body, components)
                properties.update(body.get("properties", {}))
                required.extend(body.get("required", []))
            if method.lower() == "post" and hints["toolName"] == "createRequisitionRequest":
                properties["idempotencyKey"] = {
                    "type": "string",
                    "description": "Sent as the Idempotency-Key header. A new UUID per user intent; reuse only to retry the same request.",
                }
                required.append("idempotencyKey")
            if hints.get("destructiveHint"):
                properties["ifMatch"] = {"type": "string", "description": "ETag from the latest read; sent as If-Match."}
                required.append("ifMatch")
            tools.append({
                "name": hints["toolName"],
                "title": op.get("summary", hints["toolName"]),
                "description": (op.get("summary", "") + ". " + op.get("description", "")).strip(". ") + ".",
                "inputSchema": {"type": "object", "properties": properties, "required": sorted(set(required)), "additionalProperties": False},
                "annotations": {
                    "readOnlyHint": hints["readOnlyHint"],
                    "destructiveHint": hints["destructiveHint"],
                    "idempotentHint": hints["idempotentHint"],
                    "openWorldHint": hints["openWorldHint"],
                },
                "_http": {"method": method.upper(), "path": route, "requiredScopes": op.get("x-required-scopes", [])},
            })
    json.dump({
        "$comment": "Generated from assistant-api.openapi.json by scripts/generate_mcp_tools.py. MCP tool definitions; _http shows the mapping to the REST API.",
        "tools": tools,
    }, sys.stdout, indent=2)
    sys.stdout.write("\n")


if __name__ == "__main__":
    main(sys.argv[1])
