interface RuntimeConfig {
  backendBaseUrl?: string;
  appName?: string;
  appSubtitle?: string;
  assistantName?: string;
  pageTitle?: string;
  environmentLabel?: string;
}

let runtimeConfig: RuntimeConfig = {};

const defaultConfig = {
  appName: 'InfraPilot',
  appSubtitle: 'Infrastructure Portal',
  assistantName: 'InfraPilot Assistant',
  pageTitle: 'InfraPilot | Infrastructure Portal',
};

function normalizeBaseUrl(value: string | undefined): string {
  if (!value) return '';
  return value.replace(/\/+$/, '');
}

export async function loadRuntimeConfig() {
  try {
    const response = await fetch('/config.json', { cache: 'no-store' });
    if (!response.ok) return;

    const config = (await response.json()) as RuntimeConfig;
    runtimeConfig = {
      backendBaseUrl: normalizeBaseUrl(config.backendBaseUrl),
      appName: config.appName,
      appSubtitle: config.appSubtitle,
      assistantName: config.assistantName,
      pageTitle: config.pageTitle,
      environmentLabel: config.environmentLabel,
    };
  } catch {
    runtimeConfig = {};
  }
}

function buildBackendUrl(prefix: '/api' | '/agent', path: string) {
  const normalizedPath = path.startsWith('/') ? path : `/${path}`;
  const baseUrl = normalizeBaseUrl(runtimeConfig.backendBaseUrl);
  return baseUrl ? `${baseUrl}${prefix}${normalizedPath}` : `${prefix}${normalizedPath}`;
}

export function buildApiUrl(path: string) {
  return buildBackendUrl('/api', path);
}

/**
 * The backend's absolute origin, for text that leaves the browser — a URL pasted into an agent's
 * config is resolved against nothing, so the relative paths {@link buildApiUrl} returns in the
 * default same-origin setup won't do. An empty `backendBaseUrl` means the API sits behind this
 * page's own origin.
 */
export function getBackendOrigin(): string {
  return normalizeBaseUrl(runtimeConfig.backendBaseUrl) || window.location.origin;
}

export function buildAgentUrl(path: string) {
  return buildBackendUrl('/agent', path);
}

export function getAppName() {
  return runtimeConfig.appName || defaultConfig.appName;
}

export function getAppSubtitle() {
  return runtimeConfig.appSubtitle || defaultConfig.appSubtitle;
}

export function getAssistantName() {
  return runtimeConfig.assistantName || defaultConfig.assistantName;
}

export function getPageTitle() {
  return runtimeConfig.pageTitle || defaultConfig.pageTitle;
}

export function getEnvironmentLabel(): string {
  return runtimeConfig.environmentLabel || '';
}
