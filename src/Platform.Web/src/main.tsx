import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { BrowserUtils } from '@azure/msal-browser'
import './index.css'
import App from './App.tsx'
import { AuthProvider } from '@/components/auth/AuthProvider'
import { AppErrorBoundary } from '@/components/system/ErrorBoundary'
import { StartupGate } from '@/components/system/StartupGate'
import { getPageTitle, loadRuntimeConfig } from '@/lib/runtimeConfig'
import { startUpdateChecks } from '@/lib/appUpdate'

async function bootstrap() {
  // MSAL silent token renewal loads this app (the redirectUri) inside a hidden iframe; a popup
  // sign-in would land here too. msal-browser v5 waits for the auth response over a
  // BroadcastChannel, so relay it with the redirect bridge instead of bootstrapping — otherwise
  // every iframe renewal times out. Full-page redirect responses fall through to the app, where
  // handleRedirectPromise reads them from the URL.
  if (
    /[?#].*(code=|error=)/.test(window.location.href) &&
    (window.parent !== window || BrowserUtils.isInPopup())
  ) {
    // Nothing renders here by design; don't let the boot watchdog report it as a failed start.
    window.__ipBoot?.done()
    const { broadcastResponseToMainFrame } = await import('@azure/msal-browser/redirect-bridge')
    await broadcastResponseToMainFrame().catch((err) => console.error('MSAL redirect bridge failed:', err))
    return
  }

  await loadRuntimeConfig()
  document.title = getPageTitle()

  // StartupGate loads the auth config before AuthProvider reads it, and explains (and retries) when
  // the API can't be reached; the boundary turns anything thrown while rendering into an
  // explanation instead of an empty page.
  createRoot(document.getElementById('root')!).render(
    <StrictMode>
      <AppErrorBoundary>
        <StartupGate>
          <AuthProvider>
            <App />
          </AuthProvider>
        </StartupGate>
      </AppErrorBoundary>
    </StrictMode>,
  )

  startUpdateChecks()
}

void bootstrap()
