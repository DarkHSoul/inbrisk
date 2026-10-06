using Inbrisk.Mcp;
using Inbrisk.Mcp.Daemon;

// Standalone entry: `inbrisk-mcp` — Thin Stdio Proxy with automatic fallback to McpHost.
// stdout carries the MCP protocol exclusively; logs go to stderr + JSONL.
return await ThinStdioProxy.RunAsync(args);
