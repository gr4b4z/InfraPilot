/**
 * The copy-paste setup text on the Agent access page: how to point an AI agent at InfraPortal's MCP
 * server with a personal access token.
 *
 * Pure string building, kept out of the page component so it can be checked on its own and so the
 * page module exports only components (a module mixing the two breaks React Fast Refresh).
 *
 * Every snippet authenticates the same way the pipeline API keys do — the token in an `X-Api-Key`
 * header — so the same token works for the MCP server and for the REST API.
 */

/** Stands in for the secret when no token has just been created. */
export const TOKEN_PLACEHOLDER = '<your-token>';

/** The name the server is registered under in every agent config below. */
export const MCP_SERVER_NAME = 'infraportal';

/**
 * Environment variables the snippets read the token and base URL from. The same two the repo's
 * `infrapilot-api` skill and scripts use for REST, so one setup covers both.
 */
export const ENV_URL = 'DEPLOYMENTS_URL';
export const ENV_API_KEY = 'DEPLOYMENTS_API_KEY';

/** VS Code's prompt id for the token — VS Code asks once and keeps it in its secret storage. */
const VSCODE_INPUT_ID = 'infraportal-token';

export interface AgentSnippets {
  /** The backend origin, without a trailing slash. */
  apiBase: string;
  /** The MCP endpoint. Under `/api` because that is the only prefix production's nginx forwards. */
  mcpUrl: string;
  /** The token text the snippets carry: the real secret, or {@link TOKEN_PLACEHOLDER}. */
  token: string;
  /** One `claude mcp add` command, token inline. */
  claudeCode: string;
  /** A project `.mcp.json` with no secret in it — the token comes from the environment. */
  projectMcpJson: string;
  /** Sets both environment variables for the current Windows user. */
  envPowerShell: string;
  /** Sets both environment variables in a POSIX shell. */
  envBash: string;
  /** `.vscode/mcp.json` for GitHub Copilot agent mode; VS Code prompts for the token. */
  vscodeMcpJson: string;
}

export function buildMcpUrl(apiBase: string): string {
  return `${apiBase.replace(/\/+$/, '')}/api/mcp`;
}

/**
 * Builds every snippet for one backend and, optionally, one freshly created token. A missing or
 * blank token falls back to the placeholder, so the instructions read correctly before a token
 * exists.
 */
export function buildAgentSnippets({
  apiBase,
  token,
}: {
  apiBase: string;
  token?: string | null;
}): AgentSnippets {
  const base = apiBase.replace(/\/+$/, '');
  const mcpUrl = buildMcpUrl(base);
  const secret = token?.trim() ? token.trim() : TOKEN_PLACEHOLDER;

  const claudeCode =
    `claude mcp add --transport http --scope user ${MCP_SERVER_NAME} ${mcpUrl} ` +
    `--header "X-Api-Key: ${secret}"`;

  const projectMcpJson = JSON.stringify(
    {
      mcpServers: {
        [MCP_SERVER_NAME]: {
          type: 'http',
          url: mcpUrl,
          headers: { 'X-Api-Key': `\${${ENV_API_KEY}}` },
        },
      },
    },
    null,
    2,
  );

  const envPowerShell = [
    '# Persists for your Windows user; open a new terminal afterwards',
    `[Environment]::SetEnvironmentVariable('${ENV_URL}', '${base}', 'User')`,
    `[Environment]::SetEnvironmentVariable('${ENV_API_KEY}', '${secret}', 'User')`,
    '# or, with setx:',
    `# setx ${ENV_URL} "${base}"`,
    `# setx ${ENV_API_KEY} "${secret}"`,
  ].join('\n');

  const envBash = [
    '# Add to ~/.bashrc or ~/.zshrc to persist',
    `export ${ENV_URL}="${base}"`,
    `export ${ENV_API_KEY}="${secret}"`,
  ].join('\n');

  const vscodeMcpJson = JSON.stringify(
    {
      servers: {
        [MCP_SERVER_NAME]: {
          type: 'http',
          url: mcpUrl,
          headers: { 'X-Api-Key': `\${input:${VSCODE_INPUT_ID}}` },
        },
      },
      inputs: [
        {
          type: 'promptString',
          id: VSCODE_INPUT_ID,
          description: 'InfraPortal personal token',
          password: true,
        },
      ],
    },
    null,
    2,
  );

  return {
    apiBase: base,
    mcpUrl,
    token: secret,
    claudeCode,
    projectMcpJson,
    envPowerShell,
    envBash,
    vscodeMcpJson,
  };
}
