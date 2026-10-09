import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import './index.css'
import App from './App.tsx'
import { AuthProvider } from '@/components/auth/AuthProvider'
import { AppErrorBoundary } from '@/components/system/ErrorBoundary'
import { StartupGate } from '@/components/system/StartupGate'
import { getPageTitle, loadRuntimeConfig } from '@/lib/runtimeConfig'
import { startUpdateChecks } from '@/lib/appUpdate'

async function bootstrap() {
  // MSAL silent token renewal loads this app inside a sandboxed hidden iframe.
  // Skip bootstrapping there — the auth response is handled by MSAL via the URL hash.
  if (window.parent !== window && /[?#].*(code=|error=)/.test(window.location.href)) {
    window.__ipBoot?.done()
    return
  }

  await loadRuntimeConfig()
  document.title = getPageTitle()

  // StartupGate loads the auth config before AuthProvider reads it; the boundary turns anything
  // thrown while rendering into an explanation instead of an empty page.
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
