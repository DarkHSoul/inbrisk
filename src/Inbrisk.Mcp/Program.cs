using Inbrisk.Mcp;

// Standalone entry: `inbrisk-mcp` — identical to `inbrisk mcp`.
// stdout carries the MCP protocol exclusively; logs go to stderr + JSONL.
return await McpHost.RunAsync(args);
