import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { BrowserUtils } from '@azure/msal-browser'
import './index.css'
import App from './App.tsx'
import { AuthProvider } from '@/components/auth/AuthProvider'
import { AuthErrorScreen } from '@/components/auth/AuthErrorScreen'
import { getPageTitle, loadRuntimeConfig } from '@/lib/runtimeConfig'
import { loadAuthConfig } from '@/lib/authConfig'

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
    const { broadcastResponseToMainFrame } = await import('@azure/msal-browser/redirect-bridge')
    await broadcastResponseToMainFrame().catch((err) => console.error('MSAL redirect bridge failed:', err))
    return
  }

  await loadRuntimeConfig()
  const authConfigLoaded = await loadAuthConfig()
  document.title = getPageTitle()

  createRoot(document.getElementById('root')!).render(
    <StrictMode>
      {authConfigLoaded ? (
        <AuthProvider>
          <App />
        </AuthProvider>
      ) : (
        <AuthErrorScreen
          title="Can't reach the server"
          message="Sign-in settings couldn't be loaded. The service may be restarting — try again in a moment."
        />
      )}
    </StrictMode>,
  )
}

void bootstrap()
